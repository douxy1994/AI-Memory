import XCTest
import SQLite3
@testable import AIMemory

final class NativeHistoryImporterTests: XCTestCase {
    func testCodexAndClaudeImportUseReadOnlySourceHistories() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeHistoryImporterTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let appDB = root.appendingPathComponent("app/aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: appDB)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let store = NativeConversationStore(databaseURL: appDB)

        let codexRoot = root.appendingPathComponent(".codex")
        try FileManager.default.createDirectory(at: codexRoot, withIntermediateDirectories: true)
        let rollout = codexRoot.appendingPathComponent("rollout.jsonl")
        let codexLines = [
            #"{"timestamp":"2026-07-23T10:00:00Z","type":"session_meta","payload":{"id":"codex-1","cwd":"/tmp/codex-project"}}"#,
            #"{"timestamp":"2026-07-23T10:01:00Z","type":"event_msg","payload":{"type":"user_message","message":"Implement native storage"}}"#,
            #"{"timestamp":"2026-07-23T10:02:00Z","type":"response_item","payload":{"type":"function_call","name":"exec_command","arguments":"{\"cmd\":\"pwd\"}","call_id":"call-1"}}"#,
            #"{"timestamp":"2026-07-23T10:03:00Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call-1","output":"ok"}}"#,
            #"{"timestamp":"2026-07-23T10:04:00Z","type":"event_msg","payload":{"type":"agent_message","message":"Done"}}"#,
        ]
        try Data(codexLines.joined(separator: "\n").utf8).write(to: rollout)
        try makeCodexDatabase(
            at: codexRoot.appendingPathComponent("state_5.sqlite"),
            rollout: rollout
        )

        let claudeProject = root.appendingPathComponent(".claude/projects/-tmp-claude-project")
        try FileManager.default.createDirectory(
            at: claudeProject,
            withIntermediateDirectories: true
        )
        let claude = claudeProject.appendingPathComponent("claude-1.jsonl")
        let claudeLines = [
            #"{"type":"user","uuid":"u1","timestamp":"2026-07-23T11:00:00Z","cwd":"/tmp/claude-project","message":{"role":"user","content":"Review migration"}}"#,
            #"{"type":"assistant","uuid":"a1","timestamp":"2026-07-23T11:01:00Z","message":{"role":"assistant","id":"api-1","content":[{"type":"text","text":"Reviewed"},{"type":"tool_use","id":"tool-1","name":"Read","input":{"file_path":"/tmp/a"}}]}}"#,
            #"{"type":"user","uuid":"r1","timestamp":"2026-07-23T11:02:00Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool-1","content":"contents"}]}}"#,
        ]
        try Data(claudeLines.joined(separator: "\n").utf8).write(to: claude)

        let importer = NativeHistoryImporter(store: store, home: root)
        let report = await importer.importAll()
        XCTAssertEqual(report.imported["codex"], 1)
        XCTAssertEqual(report.imported["claude"], 1)
        XCTAssertTrue(report.warnings.isEmpty)

        let codexDetail = try await store.readConversation(agent: "codex", id: "codex-1")
        XCTAssertEqual(codexDetail.projectDir, "/tmp/codex-project")
        XCTAssertEqual(codexDetail.messages.flatMap(\.toolCalls).first?.name, "exec_command")
        let claudeDetail = try await store.readConversation(agent: "claude", id: "claude-1")
        XCTAssertEqual(claudeDetail.messages.flatMap(\.toolCalls).first?.output, "contents")

        XCTAssertTrue(FileManager.default.fileExists(atPath: rollout.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: claude.path))
    }

    func testGeminiAndHermesImportPreserveToolsAndSourceFiles() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeHistoryImporterMoreTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let appDB = root.appendingPathComponent("app/aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: appDB)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let store = NativeConversationStore(databaseURL: appDB)

        let geminiChat = root.appendingPathComponent(".gemini/tmp/project/chats/session.json")
        try FileManager.default.createDirectory(
            at: geminiChat.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let gemini: [String: Any] = [
            "sessionId": "gemini-1",
            "projectPath": "/tmp/gemini-project",
            "startTime": "2026-07-23T12:00:00Z",
            "lastUpdated": "2026-07-23T12:02:00Z",
            "messages": [
                [
                    "id": "g-user",
                    "type": "user",
                    "timestamp": "2026-07-23T12:00:00Z",
                    "content": "Inspect native importer",
                ],
                [
                    "id": "g-agent",
                    "type": "gemini",
                    "timestamp": "2026-07-23T12:01:00Z",
                    "content": "Inspected",
                    "toolCalls": [[
                        "id": "g-tool",
                        "name": "write_file",
                        "args": ["file_path": "/tmp/gemini-project/a.swift"],
                        "resultDisplay": "ok",
                        "status": "success",
                    ]],
                ],
            ],
        ]
        try JSONSerialization.data(withJSONObject: gemini).write(to: geminiChat)

        let hermesDB = root.appendingPathComponent(".hermes/state.db")
        try FileManager.default.createDirectory(
            at: hermesDB.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try makeHermesDatabase(at: hermesDB)

        let importer = NativeHistoryImporter(store: store, home: root)
        let report = await importer.importAll()
        XCTAssertEqual(report.imported["gemini"], 1)
        XCTAssertEqual(report.imported["hermes"], 1)
        XCTAssertTrue(report.warnings.isEmpty)

        let geminiDetail = try await store.readConversation(agent: "gemini", id: "gemini-1")
        XCTAssertEqual(geminiDetail.messages.flatMap(\.toolCalls).first?.output, "ok")
        XCTAssertEqual(geminiDetail.fileChanges.first?.path, "/tmp/gemini-project/a.swift")
        let hermesDetail = try await store.readConversation(agent: "hermes", id: "hermes-1")
        XCTAssertEqual(hermesDetail.messages.flatMap(\.toolCalls).first?.output, "terminal output")
        XCTAssertTrue(FileManager.default.fileExists(atPath: geminiChat.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: hermesDB.path))
    }

    func testKimiAntigravityOpenCodeAndZCodeImportReadOnly() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeAdditionalHistoryTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let appDB = root.appendingPathComponent("app/aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: appDB)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let store = NativeConversationStore(databaseURL: appDB)

        let kimiSession = root.appendingPathComponent(
            ".kimi-code/sessions/work/session-kimi"
        )
        try FileManager.default.createDirectory(
            at: kimiSession.appendingPathComponent("agents/main"),
            withIntermediateDirectories: true
        )
        let kimiState: [String: Any] = [
            "title": "Kimi fixture",
            "workDir": "/tmp/kimi-project",
            "createdAt": "2026-07-23T13:00:00Z",
            "updatedAt": "2026-07-23T13:03:00Z",
        ]
        try JSONSerialization.data(withJSONObject: kimiState)
            .write(to: kimiSession.appendingPathComponent("state.json"))
        let kimiWire = [
            #"{"type":"turn.prompt","time":1784797200000,"input":[{"type":"text","text":"Edit Kimi file"}]}"#,
            #"{"type":"context.append_loop_event","time":1784797260000,"event":{"type":"tool.call","turnId":"t","step":1,"toolCallId":"k-tool","name":"write_file","args":{"file_path":"/tmp/kimi-project/a.swift"}}}"#,
            #"{"type":"context.append_loop_event","time":1784797320000,"event":{"type":"tool.result","turnId":"t","step":1,"toolCallId":"k-tool","result":{"output":"written","isError":false}}}"#,
            #"{"type":"context.append_loop_event","time":1784797380000,"event":{"type":"content.part","turnId":"t","step":1,"part":{"type":"text","text":"Done"}}}"#,
        ]
        let kimiWireURL = kimiSession.appendingPathComponent("agents/main/wire.jsonl")
        try Data(kimiWire.joined(separator: "\n").utf8).write(to: kimiWireURL)

        let antiURL = root.appendingPathComponent(
            ".gemini/antigravity/brain/anti-1/.system_generated/logs/transcript.jsonl"
        )
        try FileManager.default.createDirectory(
            at: antiURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let antiLines = [
            #"{"source":"USER_EXPLICIT","type":"MESSAGE","status":"SUCCESS","content":"<USER_REQUEST>Review Antigravity</USER_REQUEST>","created_at":"2026-07-23T14:00:00Z"}"#,
            #"{"source":"MODEL","type":"MESSAGE","status":"SUCCESS","content":"Reviewed","created_at":"2026-07-23T14:01:00Z","tool_calls":[{"name":"write_file","args":{"cwd":"/tmp/anti-project","file_path":"/tmp/anti-project/a.swift"}}]}"#,
        ]
        try Data(antiLines.joined(separator: "\n").utf8).write(to: antiURL)

        let zcodeURL = root.appendingPathComponent(
            ".zcode/v2/sessions/profile-1/task-1.json"
        )
        try FileManager.default.createDirectory(
            at: zcodeURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let zcode: [String: Any] = [
            "meta": [
                "taskId": "task-1",
                "provider": "claude",
                "title": "ZCode fixture",
                "workspacePath": "/tmp/zcode-project",
                "createdAt": 1_784_797_200_000,
                "updatedAt": 1_784_797_320_000,
                "changeSummary": [
                    "files": [["path": "/tmp/zcode-project/a.swift", "added": 2, "removed": 0]]
                ],
            ],
            "messages": [
                ["role": "user", "content": "Review ZCode", "timestamp": 1_784_797_200_000],
                [
                    "role": "assistant",
                    "content": "Reviewed",
                    "timestamp": 1_784_797_260_000,
                    "tools": [[
                        "title": "Read",
                        "input": ["file_path": "/tmp/zcode-project/a.swift"],
                        "output": "contents",
                        "status": "completed",
                    ]],
                ],
            ],
        ]
        try JSONSerialization.data(withJSONObject: zcode).write(to: zcodeURL)

        let openCodeDB = root.appendingPathComponent(
            ".local/share/opencode/opencode.db"
        )
        try FileManager.default.createDirectory(
            at: openCodeDB.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try makeOpenCodeDatabase(at: openCodeDB)

        let report = await NativeHistoryImporter(store: store, home: root).importAll()
        XCTAssertEqual(report.imported["kimi"], 1)
        XCTAssertEqual(report.imported["antigravity"], 1)
        XCTAssertEqual(report.imported["opencode"], 1)
        XCTAssertEqual(report.imported["zcode"], 1)
        XCTAssertTrue(report.warnings.isEmpty)

        let kimi = try await store.readConversation(agent: "kimi", id: "session-kimi")
        XCTAssertEqual(kimi.messages.flatMap(\.toolCalls).first?.output, "written")
        let anti = try await store.readConversation(agent: "antigravity", id: "anti-1")
        XCTAssertEqual(anti.projectDir, "/tmp/anti-project")
        let openCode = try await store.readConversation(agent: "opencode", id: "oc-1")
        XCTAssertEqual(openCode.messages.flatMap(\.toolCalls).first?.output, "ok")
        let zcodeDetail = try await store.readConversation(
            agent: "zcode",
            id: "claude:task:profile-1:task-1"
        )
        XCTAssertEqual(zcodeDetail.fileChanges.first?.changeType, "created")

        for source in [kimiWireURL, antiURL, zcodeURL, openCodeDB] {
            XCTAssertTrue(FileManager.default.fileExists(atPath: source.path))
        }
    }

    private func makeCodexDatabase(at url: URL, rollout: URL) throws {
        var raw: OpaquePointer?
        guard sqlite3_open(url.path, &raw) == SQLITE_OK, let raw else {
            throw TestError.sqlite
        }
        defer { sqlite3_close(raw) }
        let sql = """
        CREATE TABLE threads(
          id TEXT, rollout_path TEXT, cwd TEXT, title TEXT,
          created_at INTEGER, updated_at INTEGER, source TEXT
        );
        INSERT INTO threads VALUES(
          'codex-1', '\(rollout.path)', '/tmp/fallback', 'Native importer',
          1784797200, 1784797440, 'cli'
        );
        """
        guard sqlite3_exec(raw, sql, nil, nil, nil) == SQLITE_OK else {
            throw TestError.sqlite
        }
    }

    private func makeHermesDatabase(at url: URL) throws {
        var raw: OpaquePointer?
        guard sqlite3_open(url.path, &raw) == SQLITE_OK, let raw else {
            throw TestError.sqlite
        }
        defer { sqlite3_close(raw) }
        let sql = """
        CREATE TABLE sessions(
          id TEXT, title TEXT, started_at REAL, ended_at REAL,
          cwd TEXT, archived INTEGER
        );
        CREATE TABLE messages(
          id INTEGER, session_id TEXT, role TEXT, content TEXT,
          tool_calls TEXT, tool_name TEXT, timestamp REAL, active INTEGER
        );
        INSERT INTO sessions VALUES(
          'hermes-1', 'Hermes importer', 1784797200.0, 1784797320.0,
          '/tmp/hermes-project', 0
        );
        INSERT INTO messages VALUES(
          1, 'hermes-1', 'user', 'Run a command', NULL, NULL, 1784797200.0, 1
        );
        INSERT INTO messages VALUES(
          2, 'hermes-1', 'assistant', '',
          '[{"id":"h-tool","function":{"name":"terminal","arguments":"{\\"cmd\\":\\"pwd\\"}"}}]',
          NULL, 1784797260.0, 1
        );
        INSERT INTO messages VALUES(
          3, 'hermes-1', 'tool', 'terminal output', NULL, 'terminal',
          1784797320.0, 1
        );
        """
        guard sqlite3_exec(raw, sql, nil, nil, nil) == SQLITE_OK else {
            throw TestError.sqlite
        }
    }

    private func makeOpenCodeDatabase(at url: URL) throws {
        var raw: OpaquePointer?
        guard sqlite3_open(url.path, &raw) == SQLITE_OK, let raw else {
            throw TestError.sqlite
        }
        defer { sqlite3_close(raw) }
        let sql = """
        CREATE TABLE session(
          id TEXT, directory TEXT, title TEXT, time_created INTEGER,
          time_updated INTEGER, time_archived INTEGER
        );
        CREATE TABLE message(
          id TEXT, session_id TEXT, time_created INTEGER, data TEXT
        );
        CREATE TABLE part(
          id TEXT, session_id TEXT, message_id TEXT, time_created INTEGER, data TEXT
        );
        INSERT INTO session VALUES(
          'oc-1', '/tmp/opencode-project', 'OpenCode fixture',
          1784797200000, 1784797320000, NULL
        );
        INSERT INTO message VALUES(
          'oc-user', 'oc-1', 1784797200000,
          '{"role":"user","time":{"created":1784797200000}}'
        );
        INSERT INTO part VALUES(
          'p1', 'oc-1', 'oc-user', 1784797200000,
          '{"type":"text","text":"Review OpenCode"}'
        );
        INSERT INTO message VALUES(
          'oc-agent', 'oc-1', 1784797260000,
          '{"role":"assistant","time":{"created":1784797260000}}'
        );
        INSERT INTO part VALUES(
          'p2', 'oc-1', 'oc-agent', 1784797260000,
          '{"type":"tool","tool":"read","state":{"status":"completed","input":{"file_path":"/tmp/a"},"output":"ok"}}'
        );
        """
        guard sqlite3_exec(raw, sql, nil, nil, nil) == SQLITE_OK else {
            throw TestError.sqlite
        }
    }

    private enum TestError: Error {
        case sqlite
    }
}
