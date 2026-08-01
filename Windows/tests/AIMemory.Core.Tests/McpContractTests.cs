// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using System.Text.Json;
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using Xunit;

namespace AIMemory.Core.Tests;

public sealed class McpContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AIMemoryMcpContractTests", Guid.NewGuid().ToString("N"));

    public McpContractTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ProjectContextRequiresQueryAndPreservesTheMacPayloadShape()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "context.db"));
        await database.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-08-01T12:00:00Z").ToString("O");
        await using (var connection = database.OpenConnection())
        {
            var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO repos(
                  repo_id,repo_root,repo_fingerprint,git_remote,default_branch,
                  created_at,updated_at)
                VALUES('repo-1','C:\repo','fingerprint',NULL,'main',$now,$now);
                INSERT INTO approved_memories(
                  memory_id,repo_id,kind,title,value,usage_hint,status,
                  last_verified_at,created_from_candidate_id,created_at,updated_at,
                  freshness_status,freshness_score,verified_at,verified_by)
                VALUES
                  ('memory-active','repo-1','convention','Native UI','Use WinUI',
                   'Apply before UI changes','active',$now,NULL,$now,$now,
                   'fresh',1.0,$now,'test'),
                  ('memory-retired','repo-1','gotcha','Old route','Do not use',
                   'Historical record','retired',NULL,NULL,$now,$now,
                   'stale',0.1,NULL,NULL);
                INSERT INTO memory_candidates(
                  candidate_id,repo_id,kind,summary,value,why_it_matters,
                  confidence,proposed_by,status,created_at,reviewed_at)
                VALUES('candidate','repo-1','decision','Use contracts','value',
                       'Parity',0.8,'test','pending_review',$now,NULL);
                INSERT INTO conversation_chunks(
                  chunk_id,repo_id,conversation_id,chunk_type,title,body,
                  message_ids_json,ordinal,token_estimate,created_at,updated_at)
                VALUES('chunk','repo-1','internal-42','conversation','Needle','body',
                       '[]',0,1,$now,$now);
                INSERT INTO search_documents(
                  doc_id,repo_id,doc_type,doc_ref_id,title,body,updated_at)
                VALUES('search','repo-1','memory','memory-active','Native UI',
                       'Use WinUI',$now);
                INSERT INTO repo_scan_runs(
                  scan_id,repo_id,requested_repo_root,canonical_repo_root,
                  scanned_conversation_count,linked_conversation_count,
                  skipped_conversation_count,source_agents_json,
                  unmatched_project_roots_json,warnings_json,scanned_at)
                VALUES('scan','repo-1','C:\repo','C:\repo',7,5,2,'["codex"]',
                       '[{"source_agent":"claude","project_root":"C:\\old","conversation_count":2}]',
                       '[]',$now);
                INSERT INTO conversations(
                  conversation_id,repo_id,source_agent,source_conversation_id,
                  summary,started_at,updated_at,storage_path)
                VALUES('internal-42','repo-1','codex','source-42','Unrelated summary',
                       $now,$now,'C:\history\source-42.jsonl');
                INSERT INTO messages(message_id,conversation_id,role,content,timestamp)
                VALUES('message-1','internal-42','user','Needle request',$now);
                INSERT INTO file_changes(
                  file_change_id,conversation_id,message_id,path,change_type,timestamp)
                VALUES('change-1','internal-42','message-1','Program.cs','modified',$now);
                INSERT INTO handoff_packets(
                  handoff_id,repo_id,from_agent,to_agent,current_goal,done_json,
                  next_json,key_files_json,commands_json,related_memories_json,
                  related_episodes_json,created_at,status,target_profile,
                  checkpoint_id,compression_strategy,consumed_at,consumed_by)
                VALUES('handoff','repo-1','codex','claude','Finish parity',
                       '["mapped MCP"]','["run tests"]','["Program.cs"]',
                       '["dotnet test"]','[]','[]',$now,'active','desktop',
                       'checkpoint','source-backed',NULL,NULL);
                """;
            seed.Parameters.AddWithValue("$now", now);
            await seed.ExecuteNonQueryAsync();
        }

        var service = new McpProjectContextService(database);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetProjectContextAsync(@"C:\repo", " ", "", 3));

        var result = await service.GetProjectContextAsync(
            @"C:\repo", "needle", "continue implementation", 99);
        Assert.Equal(@"C:\repo", result.RepoRoot);
        Assert.Equal("needle", result.Query);
        Assert.Equal("continue implementation", result.Intent);
        Assert.Equal(["memory-active", "memory-retired"],
            result.ApprovedMemory.Select(value => value.MemoryId));
        Assert.Equal("handoff", result.RecentHandoff?.HandoffId);
        Assert.Equal(["mapped MCP"], result.RecentHandoff?.DoneItems);
        Assert.Equal(["dotnet test"], result.RecentHandoff?.UsefulCommands);
        Assert.Equal(1, result.Health.ApprovedMemoryCount);
        Assert.Equal(1, result.Health.PendingCandidateCount);
        Assert.Equal(1, result.Health.IndexedChunkCount);
        Assert.Equal(1, result.Health.SearchDocumentCount);
        Assert.Equal(7, result.Health.LatestScan?.ScannedConversationCount);
        Assert.Equal("claude", result.Health.LatestScan?
            .UnmatchedProjectRoots[0].GetProperty("source_agent").GetString());
        var history = Assert.Single(result.RelevantHistory);
        Assert.Equal("source-42", history.Id);
        Assert.Equal(1, history.MessageCount);
        Assert.Equal(1, history.FileCount);

        var payload = JsonSerializer.SerializeToElement(
            result,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            });
        Assert.Equal(
            [
                "approved_memory", "health", "intent", "query",
                "recent_handoff", "relevant_history", "repo_root",
            ],
            payload.EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal("memory-active", payload
            .GetProperty("approved_memory")[0]
            .GetProperty("memory_id").GetString());
        Assert.Equal(
            [
                "freshness_score", "freshness_status", "kind", "last_verified_at",
                "memory_id", "status", "title", "usage_hint", "value",
            ],
            payload.GetProperty("approved_memory")[0]
                .EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal("source-42", payload
            .GetProperty("relevant_history")[0]
            .GetProperty("id").GetString());
        Assert.Equal(
            [
                "created_at", "file_count", "id", "message_count", "project_dir",
                "source_agent", "summary", "updated_at",
            ],
            payload.GetProperty("relevant_history")[0]
                .EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal("handoff", payload
            .GetProperty("recent_handoff")
            .GetProperty("handoff_id").GetString());
        Assert.Equal(
            [
                "checkpoint_id", "created_at", "current_goal", "done_items",
                "from_agent", "handoff_id", "key_files", "next_items", "repo_root",
                "status", "target_profile", "to_agent", "useful_commands",
            ],
            payload.GetProperty("recent_handoff")
                .EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal(
            [
                "approved_memory_count", "indexed_chunk_count", "latest_scan",
                "pending_candidate_count", "repo_root", "search_document_count",
            ],
            payload.GetProperty("health")
                .EnumerateObject().Select(value => value.Name).Order());
    }

    [Fact]
    public async Task HistoryReadUsesSourceIdAndKeepsToolCallsInsideTheFocusedWindow()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "history.db"));
        await database.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        await using (var connection = database.OpenConnection())
        {
            var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO repos(
                  repo_id,repo_root,repo_fingerprint,git_remote,default_branch,
                  created_at,updated_at)
                VALUES('repo','C:\repo','fingerprint',NULL,'main',$now,$now);
                INSERT INTO conversations(
                  conversation_id,repo_id,source_agent,source_conversation_id,
                  summary,started_at,updated_at,storage_path)
                VALUES('internal-42','repo','codex','source-42','History detail',
                       $now,$now,'C:\history\source-42.jsonl');
                INSERT INTO messages(message_id,conversation_id,role,content,timestamp)
                VALUES
                  ('m1','internal-42','user','First',$one),
                  ('m2','internal-42','assistant','Second',$two),
                  ('m3','internal-42','user','Third',$three),
                  ('m4','internal-42','assistant','Needle tool action',$four),
                  ('m5','internal-42','user','Fifth',$five);
                INSERT INTO tool_calls(
                  tool_call_id,message_id,name,input_json,output_text,status)
                VALUES('tool-4','m4','shell','{"cmd":"dotnet test"}',
                       'passed','completed');
                INSERT INTO file_changes(
                  file_change_id,conversation_id,message_id,path,change_type,timestamp)
                VALUES('change-4','internal-42','m4','Program.cs','modified',$four);
                """;
            seed.Parameters.AddWithValue("$now", now.ToString("O"));
            seed.Parameters.AddWithValue("$one", now.AddMinutes(1).ToString("O"));
            seed.Parameters.AddWithValue("$two", now.AddMinutes(2).ToString("O"));
            seed.Parameters.AddWithValue("$three", now.AddMinutes(3).ToString("O"));
            seed.Parameters.AddWithValue("$four", now.AddMinutes(4).ToString("O"));
            seed.Parameters.AddWithValue("$five", now.AddMinutes(5).ToString("O"));
            await seed.ExecuteNonQueryAsync();
        }

        var repository = new ConversationRepository(database);
        var focused = await repository.ReadForMcpAsync(
            "source-42", "m4", "ignored", 3);
        Assert.Equal("internal-42", focused.Detail.Id);
        Assert.Equal("codex", focused.Detail.SourceAgent);
        Assert.EndsWith(
            "source-42.jsonl",
            focused.Detail.StoragePath,
            StringComparison.Ordinal);
        Assert.Equal(["m3", "m4", "m5"], focused.Messages.Select(value => value.Id));
        Assert.Equal(3, focused.ReturnedMessageCount);
        Assert.Equal("m4", focused.FocusedMessageId);
        var tool = Assert.Single(focused.Messages[1].ToolCalls);
        Assert.Equal("shell", tool.Name);
        Assert.Equal("dotnet test", tool.Input.GetProperty("cmd").GetString());
        Assert.Equal("passed", tool.Output);
        Assert.Equal("completed", tool.Status);
        Assert.Equal("Program.cs", Assert.Single(focused.Detail.FileChanges).Path);

        var queryFocused = await repository.ReadForMcpAsync(
            "source-42", null, "needle", 3);
        Assert.Equal(["m3", "m4", "m5"],
            queryFocused.Messages.Select(value => value.Id));
        Assert.Equal("", queryFocused.FocusedMessageId);

        var trailing = await repository.ReadForMcpAsync(
            "source-42", null, null, 2);
        Assert.Equal(["m4", "m5"], trailing.Messages.Select(value => value.Id));
        Assert.Equal("", trailing.FocusedMessageId);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A failed test should not be masked by a temporary SQLite cleanup.
        }
    }

}
