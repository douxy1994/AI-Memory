import Foundation
import SQLite3

enum NativeDatabaseError: LocalizedError {
    case openFailed(String)
    case statementFailed(String)
    case unsupportedSchema(Int)

    var errorDescription: String? {
        switch self {
        case .openFailed(let message):
            "无法打开 AI Memory 数据库：\(message)"
        case .statementFailed(let message):
            "AI Memory 数据库操作失败：\(message)"
        case .unsupportedSchema(let version):
            "数据库版本 \(version) 高于当前应用支持的版本。"
        }
    }
}

/// Swift-owned SQLite store for AI Memory.
///
/// The schema intentionally keeps ChatMem's table and column names so an
/// imported database can be migrated in place after it has first been copied
/// into AI Memory's independent Application Support directory.
actor NativeDatabase {
    static let schemaVersion = 1

    let url: URL
    nonisolated(unsafe) private var connection: OpaquePointer?

    init(url: URL = DataPaths.dbURL, createMigrationBackup: Bool = true) throws {
        self.url = url
        let existedBeforeOpen = FileManager.default.fileExists(atPath: url.path)
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )

        var database: OpaquePointer?
        let flags = SQLITE_OPEN_CREATE | SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX
        guard sqlite3_open_v2(url.path, &database, flags, nil) == SQLITE_OK,
              let database else {
            let message = database.map { String(cString: sqlite3_errmsg($0)) }
                ?? "unknown SQLite error"
            if let database { sqlite3_close(database) }
            throw NativeDatabaseError.openFailed(message)
        }
        connection = database
        sqlite3_busy_timeout(database, 5_000)

        do {
            try Self.execute(database, "PRAGMA journal_mode = WAL;")
            try Self.execute(database, "PRAGMA foreign_keys = ON;")
            let existingVersion = try Self.integerQuery(database, "PRAGMA user_version;")
            if createMigrationBackup, existedBeforeOpen, existingVersion < Self.schemaVersion {
                try Self.createMigrationBackup(database, sourceURL: url, version: existingVersion)
            }
            try Self.migrate(database)
        } catch {
            sqlite3_close(database)
            connection = nil
            throw error
        }
    }

    deinit {
        if let connection {
            sqlite3_close(connection)
        }
    }

    func currentSchemaVersion() throws -> Int {
        guard let connection else {
            throw NativeDatabaseError.openFailed("connection is closed")
        }
        return try Self.integerQuery(connection, "PRAGMA user_version;")
    }

    func tableNames() throws -> [String] {
        guard let connection else {
            throw NativeDatabaseError.openFailed("connection is closed")
        }
        let sql = """
        SELECT name
        FROM sqlite_master
        WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
        ORDER BY name
        """
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(connection, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else {
            throw NativeDatabaseError.statementFailed(String(cString: sqlite3_errmsg(connection)))
        }
        defer { sqlite3_finalize(statement) }

        var names: [String] = []
        while sqlite3_step(statement) == SQLITE_ROW {
            guard let value = sqlite3_column_text(statement, 0) else { continue }
            names.append(String(cString: value))
        }
        return names
    }

    private static func migrate(_ connection: OpaquePointer) throws {
        let version = try integerQuery(connection, "PRAGMA user_version;")
        guard version <= Self.schemaVersion else {
            throw NativeDatabaseError.unsupportedSchema(version)
        }
        guard version < Self.schemaVersion else { return }

        try execute(connection, "BEGIN IMMEDIATE;")
        do {
            if version < 1 {
                try execute(connection, Self.schemaV1)
                try ensureColumn(
                    connection,
                    table: "approved_memories",
                    column: "freshness_status",
                    definition: "TEXT NOT NULL DEFAULT 'unknown'"
                )
                try ensureColumn(
                    connection,
                    table: "approved_memories",
                    column: "freshness_score",
                    definition: "REAL NOT NULL DEFAULT 0.0"
                )
                try ensureColumn(
                    connection,
                    table: "approved_memories",
                    column: "verified_at",
                    definition: "TEXT"
                )
                try ensureColumn(
                    connection,
                    table: "approved_memories",
                    column: "verified_by",
                    definition: "TEXT"
                )
                try ensureColumn(
                    connection,
                    table: "handoff_packets",
                    column: "status",
                    definition: "TEXT NOT NULL DEFAULT 'draft'"
                )
                try ensureColumn(
                    connection,
                    table: "handoff_packets",
                    column: "target_profile",
                    definition: "TEXT"
                )
                try ensureColumn(
                    connection,
                    table: "handoff_packets",
                    column: "checkpoint_id",
                    definition: "TEXT"
                )
                try ensureColumn(
                    connection,
                    table: "handoff_packets",
                    column: "compression_strategy",
                    definition: "TEXT"
                )
                try ensureColumn(
                    connection,
                    table: "handoff_packets",
                    column: "consumed_at",
                    definition: "TEXT"
                )
                try ensureColumn(
                    connection,
                    table: "handoff_packets",
                    column: "consumed_by",
                    definition: "TEXT"
                )
                try ensureColumn(
                    connection,
                    table: "repo_scan_runs",
                    column: "unmatched_project_roots_json",
                    definition: "TEXT NOT NULL DEFAULT '[]'"
                )
                try execute(connection, """
                    CREATE UNIQUE INDEX IF NOT EXISTS idx_handoff_packets_checkpoint_id_unique
                    ON handoff_packets(checkpoint_id)
                    WHERE checkpoint_id IS NOT NULL;
                    """)
                try execute(connection, """
                    INSERT OR IGNORE INTO schema_migrations(version, applied_at)
                    VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                    PRAGMA user_version = 1;
                    """)
            }
            try execute(connection, "COMMIT;")
        } catch {
            try? execute(connection, "ROLLBACK;")
            throw error
        }
    }

    private static func execute(_ connection: OpaquePointer, _ sql: String) throws {
        var errorMessage: UnsafeMutablePointer<CChar>?
        let result = sqlite3_exec(connection, sql, nil, nil, &errorMessage)
        guard result == SQLITE_OK else {
            let message = errorMessage.map { String(cString: $0) }
                ?? String(cString: sqlite3_errmsg(connection))
            sqlite3_free(errorMessage)
            throw NativeDatabaseError.statementFailed(message)
        }
    }

    private static func integerQuery(_ connection: OpaquePointer, _ sql: String) throws -> Int {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(connection, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else {
            throw NativeDatabaseError.statementFailed(String(cString: sqlite3_errmsg(connection)))
        }
        defer { sqlite3_finalize(statement) }
        guard sqlite3_step(statement) == SQLITE_ROW else {
            throw NativeDatabaseError.statementFailed(String(cString: sqlite3_errmsg(connection)))
        }
        return Int(sqlite3_column_int64(statement, 0))
    }

    private static func ensureColumn(
        _ connection: OpaquePointer,
        table: String,
        column: String,
        definition: String
    ) throws {
        guard try !tableHasColumn(connection, table: table, column: column) else { return }
        try execute(connection, "ALTER TABLE \(table) ADD COLUMN \(column) \(definition);")
    }

    private static func tableHasColumn(
        _ connection: OpaquePointer,
        table: String,
        column: String
    ) throws -> Bool {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(
            connection,
            "PRAGMA table_info(\(table));",
            -1,
            &statement,
            nil
        ) == SQLITE_OK, let statement else {
            throw NativeDatabaseError.statementFailed(String(cString: sqlite3_errmsg(connection)))
        }
        defer { sqlite3_finalize(statement) }
        while sqlite3_step(statement) == SQLITE_ROW {
            guard let value = sqlite3_column_text(statement, 1) else { continue }
            if String(cString: value) == column { return true }
        }
        return false
    }

    private static func createMigrationBackup(
        _ source: OpaquePointer,
        sourceURL: URL,
        version: Int
    ) throws {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        let name = "\(sourceURL.lastPathComponent).backup-v\(version)-\(formatter.string(from: Date()))"
        let backupURL = sourceURL.deletingLastPathComponent().appendingPathComponent(name)

        var destination: OpaquePointer?
        guard sqlite3_open_v2(
            backupURL.path,
            &destination,
            SQLITE_OPEN_CREATE | SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX,
            nil
        ) == SQLITE_OK, let destination else {
            let message = destination.map { String(cString: sqlite3_errmsg($0)) }
                ?? "unable to create backup"
            if let destination { sqlite3_close(destination) }
            throw NativeDatabaseError.openFailed(message)
        }
        defer { sqlite3_close(destination) }

        guard let backup = sqlite3_backup_init(destination, "main", source, "main") else {
            throw NativeDatabaseError.statementFailed(String(cString: sqlite3_errmsg(destination)))
        }
        let result = sqlite3_backup_step(backup, -1)
        let finishResult = sqlite3_backup_finish(backup)
        guard result == SQLITE_DONE, finishResult == SQLITE_OK else {
            throw NativeDatabaseError.statementFailed(String(cString: sqlite3_errmsg(destination)))
        }
    }

    private static let schemaV1 = """
    CREATE TABLE IF NOT EXISTS schema_migrations (
        version INTEGER PRIMARY KEY,
        applied_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS repos (
        repo_id TEXT PRIMARY KEY,
        repo_root TEXT NOT NULL UNIQUE,
        repo_fingerprint TEXT NOT NULL,
        git_remote TEXT,
        default_branch TEXT,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS repo_aliases (
        alias_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        alias_root TEXT NOT NULL,
        alias_kind TEXT NOT NULL,
        confidence REAL NOT NULL,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        UNIQUE(repo_id, alias_root)
    );
    CREATE TABLE IF NOT EXISTS conversation_repo_links (
        link_id TEXT PRIMARY KEY,
        conversation_id TEXT NOT NULL,
        repo_id TEXT NOT NULL,
        confidence REAL NOT NULL,
        reason TEXT NOT NULL,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        UNIQUE(conversation_id, repo_id)
    );
    CREATE TABLE IF NOT EXISTS repo_scan_runs (
        scan_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        requested_repo_root TEXT NOT NULL,
        canonical_repo_root TEXT NOT NULL,
        scanned_conversation_count INTEGER NOT NULL,
        linked_conversation_count INTEGER NOT NULL,
        skipped_conversation_count INTEGER NOT NULL,
        source_agents_json TEXT NOT NULL DEFAULT '[]',
        unmatched_project_roots_json TEXT NOT NULL DEFAULT '[]',
        warnings_json TEXT NOT NULL DEFAULT '[]',
        scanned_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS conversations (
        conversation_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        source_agent TEXT NOT NULL,
        source_conversation_id TEXT NOT NULL,
        summary TEXT,
        started_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        storage_path TEXT
    );
    CREATE TABLE IF NOT EXISTS messages (
        message_id TEXT PRIMARY KEY,
        conversation_id TEXT NOT NULL,
        role TEXT NOT NULL,
        content TEXT NOT NULL,
        timestamp TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS conversation_chunks (
        chunk_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        conversation_id TEXT NOT NULL,
        chunk_type TEXT NOT NULL,
        title TEXT NOT NULL,
        body TEXT NOT NULL,
        message_ids_json TEXT NOT NULL DEFAULT '[]',
        ordinal INTEGER NOT NULL,
        token_estimate INTEGER NOT NULL,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS tool_calls (
        tool_call_id TEXT PRIMARY KEY,
        message_id TEXT NOT NULL,
        name TEXT NOT NULL,
        input_json TEXT NOT NULL,
        output_text TEXT,
        status TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS file_changes (
        file_change_id TEXT PRIMARY KEY,
        conversation_id TEXT NOT NULL,
        message_id TEXT NOT NULL,
        path TEXT NOT NULL,
        change_type TEXT NOT NULL,
        timestamp TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS episodes (
        episode_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        title TEXT NOT NULL,
        summary TEXT NOT NULL,
        outcome TEXT NOT NULL,
        created_at TEXT NOT NULL,
        source_conversation_id TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS memory_candidates (
        candidate_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        kind TEXT NOT NULL,
        summary TEXT NOT NULL,
        value TEXT NOT NULL,
        why_it_matters TEXT NOT NULL,
        confidence REAL NOT NULL,
        proposed_by TEXT NOT NULL,
        status TEXT NOT NULL,
        created_at TEXT NOT NULL,
        reviewed_at TEXT
    );
    CREATE TABLE IF NOT EXISTS approved_memories (
        memory_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        kind TEXT NOT NULL,
        title TEXT NOT NULL,
        value TEXT NOT NULL,
        usage_hint TEXT NOT NULL,
        status TEXT NOT NULL,
        last_verified_at TEXT,
        created_from_candidate_id TEXT,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        freshness_status TEXT NOT NULL DEFAULT 'unknown',
        freshness_score REAL NOT NULL DEFAULT 0.0,
        verified_at TEXT,
        verified_by TEXT
    );
    CREATE TABLE IF NOT EXISTS handoff_packets (
        handoff_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        from_agent TEXT NOT NULL,
        to_agent TEXT NOT NULL,
        current_goal TEXT NOT NULL,
        done_json TEXT NOT NULL,
        next_json TEXT NOT NULL,
        key_files_json TEXT NOT NULL,
        commands_json TEXT NOT NULL,
        related_memories_json TEXT NOT NULL DEFAULT '[]',
        related_episodes_json TEXT NOT NULL DEFAULT '[]',
        created_at TEXT NOT NULL,
        status TEXT NOT NULL DEFAULT 'draft',
        target_profile TEXT,
        checkpoint_id TEXT,
        compression_strategy TEXT,
        consumed_at TEXT,
        consumed_by TEXT
    );
    CREATE TABLE IF NOT EXISTS checkpoints (
        checkpoint_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        conversation_id TEXT NOT NULL,
        source_agent TEXT NOT NULL,
        status TEXT NOT NULL DEFAULT 'active',
        summary TEXT NOT NULL,
        resume_command TEXT,
        metadata_json TEXT NOT NULL DEFAULT '{}',
        handoff_id TEXT,
        created_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS agent_runs (
        run_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        source_agent TEXT NOT NULL,
        task_hint TEXT,
        status TEXT NOT NULL,
        summary TEXT NOT NULL,
        started_at TEXT NOT NULL,
        ended_at TEXT
    );
    CREATE TABLE IF NOT EXISTS run_events (
        event_id TEXT PRIMARY KEY,
        run_id TEXT NOT NULL,
        event_type TEXT NOT NULL,
        detail_json TEXT NOT NULL,
        created_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS artifacts (
        artifact_id TEXT PRIMARY KEY,
        run_id TEXT NOT NULL,
        artifact_type TEXT NOT NULL,
        title TEXT NOT NULL,
        summary TEXT NOT NULL,
        body TEXT,
        file_path TEXT,
        trust_state TEXT NOT NULL,
        created_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS evidence_refs (
        evidence_id TEXT PRIMARY KEY,
        owner_type TEXT NOT NULL,
        owner_id TEXT NOT NULL,
        conversation_id TEXT,
        message_id TEXT,
        tool_call_id TEXT,
        file_change_id TEXT,
        excerpt TEXT NOT NULL,
        created_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS memory_conflicts (
        conflict_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        candidate_id TEXT NOT NULL,
        memory_id TEXT NOT NULL,
        reason TEXT NOT NULL,
        status TEXT NOT NULL,
        created_at TEXT NOT NULL,
        resolved_at TEXT,
        UNIQUE(candidate_id, memory_id)
    );
    CREATE TABLE IF NOT EXISTS memory_merge_proposals (
        proposal_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        candidate_id TEXT NOT NULL,
        target_memory_id TEXT NOT NULL,
        proposed_title TEXT NOT NULL,
        proposed_value TEXT NOT NULL,
        proposed_usage_hint TEXT NOT NULL,
        risk_note TEXT,
        proposed_by TEXT NOT NULL,
        status TEXT NOT NULL,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        UNIQUE(candidate_id, target_memory_id)
    );
    CREATE TABLE IF NOT EXISTS memory_entities (
        entity_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        name TEXT NOT NULL,
        normalized_name TEXT NOT NULL,
        kind TEXT NOT NULL,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        UNIQUE(repo_id, normalized_name)
    );
    CREATE TABLE IF NOT EXISTS memory_entity_links (
        link_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        entity_id TEXT NOT NULL,
        owner_type TEXT NOT NULL,
        owner_id TEXT NOT NULL,
        relationship TEXT NOT NULL,
        created_at TEXT NOT NULL,
        UNIQUE(repo_id, entity_id, owner_type, owner_id, relationship)
    );
    CREATE TABLE IF NOT EXISTS wiki_pages (
        page_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        slug TEXT NOT NULL,
        title TEXT NOT NULL,
        body TEXT NOT NULL,
        status TEXT NOT NULL,
        source_memory_ids_json TEXT NOT NULL DEFAULT '[]',
        source_episode_ids_json TEXT NOT NULL DEFAULT '[]',
        last_built_at TEXT NOT NULL,
        last_verified_at TEXT,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        UNIQUE(repo_id, slug)
    );
    CREATE TABLE IF NOT EXISTS search_documents (
        doc_id TEXT PRIMARY KEY,
        repo_id TEXT NOT NULL,
        doc_type TEXT NOT NULL,
        doc_ref_id TEXT NOT NULL,
        title TEXT NOT NULL,
        body TEXT NOT NULL,
        updated_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS document_embeddings (
        doc_id TEXT NOT NULL,
        repo_id TEXT NOT NULL,
        embedding_model TEXT NOT NULL,
        dimensions INTEGER NOT NULL,
        vector_json TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        PRIMARY KEY (doc_id, embedding_model)
    );
    CREATE INDEX IF NOT EXISTS idx_conversation_chunks_repo_updated
        ON conversation_chunks(repo_id, updated_at);
    CREATE INDEX IF NOT EXISTS idx_conversation_chunks_conversation
        ON conversation_chunks(conversation_id, ordinal);
    CREATE INDEX IF NOT EXISTS idx_repo_aliases_alias_root
        ON repo_aliases(alias_root);
    CREATE INDEX IF NOT EXISTS idx_conversation_repo_links_repo
        ON conversation_repo_links(repo_id, confidence);
    CREATE INDEX IF NOT EXISTS idx_repo_scan_runs_repo_scanned_at
        ON repo_scan_runs(repo_id, scanned_at DESC);
    CREATE INDEX IF NOT EXISTS idx_document_embeddings_repo_model
        ON document_embeddings(repo_id, embedding_model, dimensions);
    CREATE INDEX IF NOT EXISTS idx_memory_conflicts_repo_status
        ON memory_conflicts(repo_id, status);
    CREATE INDEX IF NOT EXISTS idx_memory_merge_proposals_repo_status
        ON memory_merge_proposals(repo_id, status);
    CREATE INDEX IF NOT EXISTS idx_memory_entity_links_repo_owner
        ON memory_entity_links(repo_id, owner_type, owner_id);
    CREATE VIRTUAL TABLE IF NOT EXISTS search_documents_fts USING fts5(
        doc_id UNINDEXED,
        title,
        body
    );
    """
}
