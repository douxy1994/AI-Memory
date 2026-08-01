// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using System.Text.Json;
using AIMemory.Core.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AIMemory.Core.Tests;

public sealed class ConversationDetailRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AIMemoryConversationDetailTests",
        Guid.NewGuid().ToString("N"));

    public ConversationDetailRepositoryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ExportForDetailResolvesSourceIdAndRetainsToolPayload()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "detail.db"));
        await database.InitializeAsync();
        var timestamp = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        await using (var connection = database.OpenConnection())
        {
            var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO repos(
                  repo_id,repo_root,repo_fingerprint,created_at,updated_at)
                VALUES('repo','C:\work\sample','fingerprint',$now,$now);
                INSERT INTO conversations(
                  conversation_id,repo_id,source_agent,source_conversation_id,
                  summary,started_at,updated_at,storage_path)
                VALUES('internal-id','repo','codex','source-id','Detail',
                       $now,$now,'C:\history\source-id.jsonl');
                INSERT INTO messages(message_id,conversation_id,role,content,timestamp)
                VALUES
                  ('message-user','internal-id','user','Need implementation',$now),
                  ('message-agent','internal-id','assistant','I used a tool',$later);
                INSERT INTO tool_calls(
                  tool_call_id,message_id,name,input_json,output_text,status)
                VALUES('tool-1','message-agent','shell','{"cmd":"dotnet test"}',
                       'passed','completed');
                INSERT INTO file_changes(
                  file_change_id,conversation_id,message_id,path,change_type,timestamp)
                VALUES('change-1','internal-id','message-agent','Program.cs',
                       'modified',$later);
                """;
            seed.Parameters.AddWithValue("$now", timestamp.ToString("O"));
            seed.Parameters.AddWithValue(
                "$later",
                timestamp.AddMinutes(1).ToString("O"));
            await seed.ExecuteNonQueryAsync();
        }

        var detail = await new ConversationRepository(database).ExportAsync(
            "source-id");

        Assert.Equal("internal-id", detail.Id);
        Assert.Equal("codex", detail.SourceAgent);
        Assert.Equal("Detail", detail.Summary);
        Assert.Equal("codex resume internal-id", detail.ResumeCommand);
        Assert.Equal(["message-user", "message-agent"],
            detail.Messages.Select(value => value.Id));
        var tool = Assert.Single(detail.Messages[1].ToolCalls);
        Assert.Equal("shell", tool.Name);
        Assert.Equal("dotnet test", tool.Input.GetProperty("cmd").GetString());
        Assert.Equal("passed", tool.Output);
        Assert.Equal("completed", tool.Status);
        Assert.Equal("Program.cs", Assert.Single(detail.FileChanges).Path);
    }

    [Fact]
    public async Task ExportForDetailKeepsReadingWhenOneToolInputIsMalformed()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "malformed.db"));
        await database.InitializeAsync();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var connection = database.OpenConnection())
        {
            var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO repos(
                  repo_id,repo_root,repo_fingerprint,created_at,updated_at)
                VALUES('repo','C:\work\malformed','fingerprint',$now,$now);
                INSERT INTO conversations(
                  conversation_id,repo_id,source_agent,source_conversation_id,
                  summary,started_at,updated_at,storage_path)
                VALUES('internal-malformed','repo','claude','source-malformed',
                       'Malformed input survives',$now,$now,NULL);
                INSERT INTO messages(message_id,conversation_id,role,content,timestamp)
                VALUES('message','internal-malformed','assistant','Still readable',$now);
                INSERT INTO tool_calls(
                  tool_call_id,message_id,name,input_json,output_text,status)
                VALUES('tool','message','read_file','{not-valid-json',NULL,'failed');
                """;
            seed.Parameters.AddWithValue("$now", now);
            await seed.ExecuteNonQueryAsync();
        }

        var repository = new ConversationRepository(database);
        var detail = await repository.ExportAsync("source-malformed");

        Assert.Equal("internal-malformed", detail.Id);
        Assert.Equal("Still readable", Assert.Single(detail.Messages).Content);
        Assert.Equal(JsonValueKind.Null, Assert.Single(
            detail.Messages[0].ToolCalls).Input.ValueKind);

        var missing = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => repository.ExportAsync("missing-conversation"));
        Assert.Contains("missing-conversation", missing.Message);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
