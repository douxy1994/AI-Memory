using System.Text.Json;
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;

namespace AIMemory.Mcp;

public static class Program
{
    // MCP and JSON-RPC field names are camelCase by specification. Tool
    // payloads intentionally stay snake_case to match the macOS helper and
    // the existing AI Memory tool contract.
    private static readonly JsonSerializerOptions RpcJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions ToolPayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static async Task Main()
    {
        DataPaths.EnsureDirectories();
        var database = new AIMemoryDatabase();
        await database.InitializeAsync();
        var query = new MemoryQueryService(database);
        var conversations = new ConversationRepository(database);
        var history = new NativeHistoryImportService(conversations);
        var diagnostics = new DiagnosticsService(database);

        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            object? response;
            try
            {
                using var document = JsonDocument.Parse(line);
                response = await HandleAsync(
                    document.RootElement,
                    query,
                    conversations,
                    history,
                    diagnostics);
            }
            catch (Exception exception)
            {
                response = Error(null, -32603, exception.Message);
            }
            if (response is null) continue;
            await Console.Out.WriteLineAsync(
                JsonSerializer.Serialize(response, RpcJsonOptions));
            await Console.Out.FlushAsync();
        }
    }

    private static async Task<object?> HandleAsync(
        JsonElement request,
        MemoryQueryService query,
        ConversationRepository conversations,
        NativeHistoryImportService history,
        DiagnosticsService diagnostics)
    {
        var hasId = request.TryGetProperty("id", out var idValue);
        var id = hasId
            ? idValue.Clone()
            : (JsonElement?)null;
        var method = request.GetProperty("method").GetString() ?? "";
        if (!hasId)
        {
            return null;
        }
        if (method == "initialize")
        {
            return Success(id, new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "aimemory", version = "0.1.0" },
            });
        }
        if (method == "tools/list")
        {
            return Success(id, new { tools = ToolDefinitions });
        }
        if (method != "tools/call")
        {
            return Error(id, -32601, $"Unknown method: {method}");
        }

        var parameters = request.GetProperty("params");
        var name = parameters.GetProperty("name").GetString() ?? "";
        var arguments = parameters.TryGetProperty("arguments", out var values)
            ? values
            : JsonSerializer.SerializeToElement(new { });
        object result = name switch
        {
            "get_repo_memory" => await GetRepoMemoryAsync(query, arguments),
            "get_project_context" => await GetContextAsync(query, arguments),
            "get_repo_memory_health" => await GetHealthAsync(
                query,
                diagnostics,
                arguments),
            "import_all_local_history" => await ImportHistoryAsync(history),
            "scan_repo_conversations" => await ScanRepositoryAsync(
                query,
                history,
                arguments),
            "search_repo_history" => await SearchAsync(query, arguments),
            "read_history_conversation" => await ReadAsync(conversations, arguments),
            "detect_agent_integrations" => new AgentCatalog().Detect(),
            _ => throw new InvalidOperationException($"Unknown tool: {name}"),
        };
        var text = JsonSerializer.Serialize(result, ToolPayloadJsonOptions);
        return Success(id, new
        {
            content = new[] { new { type = "text", text } },
            isError = false,
        });
    }

    private static async Task<object> GetRepoMemoryAsync(
        MemoryQueryService service,
        JsonElement arguments)
    {
        var root = Required(arguments, "repo_root");
        var context = await service.GetProjectContextAsync(
            root,
            Optional(arguments, "task_hint"),
            20);
        return new
        {
            repo_root = root,
            task_hint = Optional(arguments, "task_hint"),
            memories = context.ApprovedMemory,
        };
    }

    private static async Task<object> GetContextAsync(
        MemoryQueryService service,
        JsonElement arguments)
    {
        var root = Required(arguments, "repo_root");
        var query = Optional(arguments, "query");
        var limit = OptionalInt(arguments, "limit", 3);
        return await service.GetProjectContextAsync(root, query, limit);
    }

    private static async Task<object> SearchAsync(
        MemoryQueryService service,
        JsonElement arguments) =>
        await service.SearchAsync(
            Required(arguments, "repo_root"),
            Required(arguments, "query"),
            OptionalInt(arguments, "limit", 3));

    private static async Task<object> GetHealthAsync(
        MemoryQueryService query,
        DiagnosticsService diagnostics,
        JsonElement arguments)
    {
        var root = Required(arguments, "repo_root");
        var context = await query.GetProjectContextAsync(root, "", 3);
        var report = await diagnostics.CollectAsync("0.1.0");
        return new
        {
            repo_root = root,
            approved_memory_count = context.ApprovedMemory.Count,
            checkpoint_count = context.RecentCheckpoints.Count,
            search_document_count = report.Messages,
            indexed_conversation_count = report.Conversations,
            pending_candidate_count = report.PendingCandidates,
            schema_version = report.SchemaVersion,
        };
    }

    private static async Task<object> ImportHistoryAsync(
        NativeHistoryImportService history)
    {
        var report = await history.ImportAllAsync();
        return new
        {
            imported = report.Imported,
            imported_count = report.Imported.Values.Sum(),
            warnings = report.Warnings,
        };
    }

    private static async Task<object> ScanRepositoryAsync(
        MemoryQueryService query,
        NativeHistoryImportService history,
        JsonElement arguments)
    {
        var root = Required(arguments, "repo_root");
        var report = await history.ImportAllAsync();
        var context = await query.GetProjectContextAsync(root, "", 3);
        return new
        {
            repo_root = root,
            imported = report.Imported,
            warnings = report.Warnings,
            approved_memory_count = context.ApprovedMemory.Count,
            checkpoint_count = context.RecentCheckpoints.Count,
            relevant_history_count = context.RelevantHistory.Count,
        };
    }

    private static async Task<object> ReadAsync(
        ConversationRepository repository,
        JsonElement arguments)
    {
        var id = Required(arguments, "conversation_id");
        return new
        {
            conversation_id = id,
            messages = await repository.ReadMessagesAsync(id),
        };
    }

    private static string Required(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property)
        && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new ArgumentException($"Missing required argument: {key}");

    private static string Optional(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property)
            ? property.GetString() ?? ""
            : "";

    private static int OptionalInt(JsonElement value, string key, int fallback) =>
        value.TryGetProperty(key, out var property)
        && property.TryGetInt32(out var result)
            ? result
            : fallback;

    private static object Success(JsonElement? id, object result) =>
        new { jsonrpc = "2.0", id, result };

    private static object Error(JsonElement? id, int code, string message) =>
        new { jsonrpc = "2.0", id, error = new { code, message } };

    private static readonly object[] ToolDefinitions =
    [
        Tool(
            "get_repo_memory",
            "Return compact approved startup rules for an agent.",
            new
            {
                repo_root = StringSchema(),
                task_hint = StringSchema(),
            },
            ["repo_root"]),
        Tool(
            "get_project_context",
            "Return approved memory, checkpoints and compact local history.",
            new { repo_root = StringSchema(), query = StringSchema(), limit = IntSchema() },
            ["repo_root"]),
        Tool(
            "get_repo_memory_health",
            "Return local-history and memory diagnostics.",
            new { repo_root = StringSchema() },
            ["repo_root"]),
        Tool(
            "import_all_local_history",
            "Import supported local agent histories into AI Memory's independent index.",
            new { },
            []),
        Tool(
            "scan_repo_conversations",
            "Scan local histories and return repository memory health.",
            new { repo_root = StringSchema() },
            ["repo_root"]),
        Tool(
            "search_repo_history",
            "Search indexed local repository history.",
            new { repo_root = StringSchema(), query = StringSchema(), limit = IntSchema() },
            ["repo_root", "query"]),
        Tool(
            "read_history_conversation",
            "Read messages from a local indexed conversation.",
            new { repo_root = StringSchema(), conversation_id = StringSchema() },
            ["repo_root", "conversation_id"]),
        Tool(
            "detect_agent_integrations",
            "Detect installed AI agents and CLIs without enabling missing products.",
            new { },
            []),
    ];

    private static object Tool(
        string name,
        string description,
        object properties,
        string[] required) =>
        new
        {
            name,
            description,
            inputSchema = new { type = "object", properties, required },
        };

    private static object StringSchema() => new { type = "string" };
    private static object IntSchema() =>
        new { type = "integer", minimum = 1, maximum = 50 };
}
