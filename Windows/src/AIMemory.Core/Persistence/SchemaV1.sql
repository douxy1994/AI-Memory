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
CREATE INDEX IF NOT EXISTS idx_conversations_updated_at
    ON conversations(updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_messages_conversation_id
    ON messages(conversation_id);
CREATE INDEX IF NOT EXISTS idx_file_changes_conversation_id
    ON file_changes(conversation_id);
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
INSERT OR IGNORE INTO schema_migrations(version, applied_at)
VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
PRAGMA user_version = 1;
