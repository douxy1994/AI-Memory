// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import Foundation

/// Source agent identifiers. Mirrors the 8 agent keys exposed by
/// `aimemory-bridge`'s `detect_conversation_sources`.
enum AgentKind: String, CaseIterable, Identifiable, Codable {
    case claude
    case codex
    case gemini
    case antigravity
    case opencode
    case zcode
    case hermes
    case kimi

    var id: String { rawValue }

    /// Native stores that AI Memory can write and immediately re-import for
    /// migration verification. The UI intersects this capability with the
    /// sources detected on the current Mac instead of presenting a fixed list.
    var supportsNativeMigrationTarget: Bool {
        switch self {
        case .claude, .codex, .gemini, .opencode, .kimi: true
        case .antigravity, .zcode, .hermes: false
        }
    }

    var label: String {
        switch self {
        case .claude: "Claude"
        case .codex: "Codex"
        case .gemini: "Gemini"
        case .antigravity: "Antigravity"
        case .opencode: "OpenCode"
        case .zcode: "ZCode"
        case .hermes: "Hermes"
        case .kimi: "Kimi Code"
        }
    }

    /// Short product copy used in the workbench.
    var subtitle: String {
        switch self {
        case .claude: "Anthropic Claude Code 本地会话"
        case .codex: "OpenAI Codex CLI 本地会话"
        case .gemini: "Google Gemini CLI 本地会话"
        case .antigravity: "Google Antigravity CLI 本地会话"
        case .opencode: "OpenCode 本地会话"
        case .zcode: "ZCode 本地会话"
        case .hermes: "Hermes Agent 本地会话"
        case .kimi: "Moonshot Kimi Code 本地会话"
        }
    }
}

/// Top-level navigation destinations in the workspace pane.
enum WorkspaceDestination: String, Hashable, CaseIterable, Identifiable {
    case workbench
    case conversation
    case review
    case history
    case settings
    case favorites
    case trash
    case help

    var id: String { rawValue }

    var title: String {
        switch self {
        case .workbench: "工作台"
        case .conversation: "对话"
        case .review: "待复核"
        case .history: "历史"
        case .settings: "设置"
        case .favorites: "收藏"
        case .trash: "回收站"
        case .help: "帮助"
        }
    }
}

/// Memory drawer tab selection.
enum MemoryDrawerTab: String, CaseIterable, Identifiable, Hashable {
    case review = "Review"
    case rules = "Rules"
    case wiki = "Wiki"
    case continuation = "Continue"

    var id: String { rawValue }

    var label: String {
        switch self {
        case .review: "候选规则"
        case .rules: "已批准规则"
        case .wiki: "Wiki"
        case .continuation: "继续"
        }
    }
}

// MARK: - Conversation domain (decoded from bridge JSON-RPC results)

/// One source-agent conversation, as returned by `list_conversations`.
struct ConversationSummary: Identifiable, Hashable, Codable {
    let id: String
    let sourceAgent: String
    let projectDir: String
    let createdAt: String
    let updatedAt: String
    let summary: String?
    let messageCount: Int
    let fileCount: Int

    var agentKind: AgentKind? { AgentKind(rawValue: sourceAgent) }

    /// Title shown in the list. Falls back to project leaf or first-user message.
    var displayTitle: String {
        if let summary, !summary.isEmpty {
            return String(summary.prefix(80))
        }
        return projectLeaf
    }

    var projectLeaf: String {
        let trimmed = projectDir.trimmingCharacters(in: .init(charactersIn: "/"))
        return (trimmed as NSString).lastPathComponent.isEmpty
            ? projectDir
            : (trimmed as NSString).lastPathComponent
    }

    enum CodingKeys: String, CodingKey {
        case id
        case sourceAgent = "source_agent"
        case projectDir = "project_dir"
        case createdAt = "created_at"
        case updatedAt = "updated_at"
        case summary
        case messageCount = "message_count"
        case fileCount = "file_count"
    }
}

/// A full conversation including messages and file changes, as returned by
/// `read_conversation`.
struct ConversationDetail: Hashable, Codable {
    let id: String
    let sourceAgent: String
    let projectDir: String
    let createdAt: String
    let updatedAt: String
    let summary: String?
    let storagePath: String?
    let resumeCommand: String?
    let messages: [ConversationMessage]
    let fileChanges: [FileChange]

    enum CodingKeys: String, CodingKey {
        case id
        case sourceAgent = "source_agent"
        case projectDir = "project_dir"
        case createdAt = "created_at"
        case updatedAt = "updated_at"
        case summary
        case storagePath = "storage_path"
        case resumeCommand = "resume_command"
        case messages
        case fileChanges = "file_changes"
    }
}

struct ConversationMessage: Identifiable, Hashable, Codable {
    let id: String
    let timestamp: String
    let role: String
    let content: String
    let toolCalls: [ToolCall]
    let metadata: [String: JSONValue]?

    var roleLabel: String {
        switch role.lowercased() {
        case "user": "用户"
        case "assistant": "助手"
        case "system": "系统"
        default: role.capitalized
        }
    }

    enum CodingKeys: String, CodingKey {
        case id, timestamp, role, content, metadata
        case toolCalls = "tool_calls"
    }
}

struct ToolCall: Identifiable, Hashable, Codable {
    /// Synthesized id; the bridge output has no per-call id.
    let id: String
    let name: String
    let input: JSONValue
    let output: String?
    let status: String

    init(id: String = UUID().uuidString, name: String, input: JSONValue, output: String?, status: String) {
        self.id = id
        self.name = name
        self.input = input
        self.output = output
        self.status = status
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.id = (try? c.decode(String.self, forKey: .id)) ?? UUID().uuidString
        self.name = try c.decode(String.self, forKey: .name)
        self.input = try c.decode(JSONValue.self, forKey: .input)
        self.output = try c.decodeIfPresent(String.self, forKey: .output)
        self.status = try c.decode(String.self, forKey: .status)
    }
}

struct FileChange: Identifiable, Hashable, Codable {
    var id: String { path + "|" + changeType + "|" + timestamp }
    let path: String
    let changeType: String
    let timestamp: String
    let messageId: String?

    var changeTypeLabel: String {
        switch changeType.lowercased() {
        case "created", "added": "新增"
        case "modified": "修改"
        case "deleted", "removed": "删除"
        default: changeType
        }
    }

    var changeIcon: String {
        switch changeType.lowercased() {
        case "created", "added": "plus.circle"
        case "modified": "pencil.circle"
        case "deleted", "removed": "minus.circle"
        default: "circle.dashed"
        }
    }

    enum CodingKeys: String, CodingKey {
        case path
        case changeType = "change_type"
        case timestamp
        case messageId = "message_id"
    }
}

struct UpgradeReadinessCheck: Identifiable, Hashable, Codable {
    let key: String
    let label: String
    let status: String
    let detail: String

    var id: String { key }
}

struct UpgradeReadinessReport: Hashable, Codable {
    let status: String
    let summary: String
    let checks: [UpgradeReadinessCheck]
    let warnings: [String]
}

// MARK: - Lightweight, lenient JSON value used for tool input / metadata.

/// A type-erased JSON value that decodes any JSON without throwing, so unknown
/// metadata from the Rust bridge never breaks decoding.
indirect enum JSONValue: Hashable, Codable {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { self = .null }
        else if let v = try? c.decode(Bool.self) { self = .bool(v) }
        else if let v = try? c.decode(Double.self) { self = .number(v) }
        else if let v = try? c.decode(String.self) { self = .string(v) }
        else if let v = try? c.decode([JSONValue].self) { self = .array(v) }
        else if let v = try? c.decode([String: JSONValue].self) { self = .object(v) }
        else { self = .null }
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .null: try c.encodeNil()
        case .bool(let v): try c.encode(v)
        case .number(let v): try c.encode(v)
        case .string(let v): try c.encode(v)
        case .array(let v): try c.encode(v)
        case .object(let v): try c.encode(v)
        }
    }

    /// Pretty single-line preview for tool-call chips.
    var preview: String {
        switch self {
        case .null: "null"
        case .bool(let v): String(v)
        case .number(let v): String(v)
        case .string(let v): v
        case .array(let v): "[" + v.map(\.preview).joined(separator: ", ") + "]"
        case .object(let v):
            "{" + v.map { "\($0.key): \($0.value.preview)" }.joined(separator: ", ") + "}"
        }
    }
}

// MARK: - Status payloads

struct ConversationSourceStatus: Identifiable, Hashable, Codable {
    let agent: String
    let label: String
    let available: Bool
    var id: String { agent }
    var agentKind: AgentKind? { AgentKind(rawValue: agent) }
}

/// Best-effort repository health summary decoded from the bridge's
/// `get_repo_memory_health` payload. Unknown/extra fields are ignored.
struct RepoHealth: Hashable, Codable {
    let repoRoot: String?
    let approvedMemoryCount: Int?
    let pendingCandidateCount: Int?
    let indexedChunkCount: Int?
    let searchDocumentCount: Int?
    let latestScan: LatestScan?

    struct LatestScan: Hashable, Codable {
        let scannedConversationCount: Int?
        let linkedConversationCount: Int?
        let skippedConversationCount: Int?
        let unmatchedProjectRoots: [UnmatchedRoot]?

        struct UnmatchedRoot: Hashable, Codable {
            let sourceAgent: String?
            let projectRoot: String?
            let conversationCount: Int?

            enum CodingKeys: String, CodingKey {
                case sourceAgent = "source_agent"
                case projectRoot = "project_root"
                case conversationCount = "conversation_count"
            }

            init(from decoder: Decoder) throws {
                let c = try decoder.container(keyedBy: CodingKeys.self)
                sourceAgent = try? c.decodeIfPresent(String.self, forKey: .sourceAgent)
                projectRoot = try? c.decodeIfPresent(String.self, forKey: .projectRoot)
                conversationCount = try? c.decodeIfPresent(Int.self, forKey: .conversationCount)
            }
        }

        enum CodingKeys: String, CodingKey {
            case scannedConversationCount = "scanned_conversation_count"
            case linkedConversationCount = "linked_conversation_count"
            case skippedConversationCount = "skipped_conversation_count"
            case unmatchedProjectRoots = "unmatched_project_roots"
        }

        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            scannedConversationCount = try? c.decodeIfPresent(Int.self, forKey: .scannedConversationCount)
            linkedConversationCount = try? c.decodeIfPresent(Int.self, forKey: .linkedConversationCount)
            skippedConversationCount = try? c.decodeIfPresent(Int.self, forKey: .skippedConversationCount)
            unmatchedProjectRoots = try? c.decodeIfPresent([UnmatchedRoot].self, forKey: .unmatchedProjectRoots)
        }
    }

    enum CodingKeys: String, CodingKey {
        case repoRoot = "repo_root"
        case approvedMemoryCount = "approved_memory_count"
        case pendingCandidateCount = "pending_candidate_count"
        case indexedChunkCount = "indexed_chunk_count"
        case searchDocumentCount = "search_document_count"
        case latestScan = "latest_scan"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        repoRoot = try? c.decodeIfPresent(String.self, forKey: .repoRoot)
        approvedMemoryCount = try? c.decodeIfPresent(Int.self, forKey: .approvedMemoryCount)
        pendingCandidateCount = try? c.decodeIfPresent(Int.self, forKey: .pendingCandidateCount)
        indexedChunkCount = try? c.decodeIfPresent(Int.self, forKey: .indexedChunkCount)
        searchDocumentCount = try? c.decodeIfPresent(Int.self, forKey: .searchDocumentCount)
        latestScan = try? c.decodeIfPresent(LatestScan.self, forKey: .latestScan)
    }
}

// MARK: - Memory governance models (decoded from bridge JSON-RPC results)
// Field names match the Rust response structs in
// ChatMem/src-tauri/src/chatmem_memory/models.rs.

/// A traceable evidence reference attached to candidates, memories, episodes.
struct EvidenceRef: Hashable, Codable {
    let evidenceID: String?
    let conversationID: String?
    let messageID: String?
    let toolCallID: String?
    let fileChangeID: String?
    let excerpt: String

    enum CodingKeys: String, CodingKey {
        case evidenceID = "evidence_id"
        case conversationID = "conversation_id"
        case messageID = "message_id"
        case toolCallID = "tool_call_id"
        case fileChangeID = "file_change_id"
        case excerpt
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        evidenceID = try? c.decodeIfPresent(String.self, forKey: .evidenceID)
        conversationID = try? c.decodeIfPresent(String.self, forKey: .conversationID)
        messageID = try? c.decodeIfPresent(String.self, forKey: .messageID)
        toolCallID = try? c.decodeIfPresent(String.self, forKey: .toolCallID)
        fileChangeID = try? c.decodeIfPresent(String.self, forKey: .fileChangeID)
        excerpt = (try? c.decodeIfPresent(String.self, forKey: .excerpt)) ?? ""
    }
}

/// A pending startup-rule candidate, from `list_memory_candidates`.
struct MemoryCandidate: Identifiable, Hashable, Codable {
    let candidateID: String
    let kind: String
    let summary: String
    let value: String
    let whyItMatters: String
    let confidence: Double
    let proposedBy: String
    let status: String
    let createdAt: String
    let evidenceRefs: [EvidenceRef]
    let mergeSuggestion: JSONValue?
    let conflictSuggestion: JSONValue?

    var id: String { candidateID }

    var kindLabel: String {
        switch kind.lowercased() {
        case "command": "命令"
        case "convention": "约定"
        case "decision": "决策"
        case "gotcha": "注意事项"
        case "preference": "偏好"
        default: kind
        }
    }

    var statusLabel: String {
        switch status.lowercased() {
        case "pending_review", "pending": "待审"
        case "approved": "已批准"
        case "rejected": "已拒绝"
        case "snoozed": "暂缓"
        case "merged": "已合并"
        default: status
        }
    }

    var isActionable: Bool { status.lowercased() == "pending_review" || status.lowercased() == "pending" }

    enum CodingKeys: String, CodingKey {
        case candidateID = "candidate_id"
        case kind, summary, value
        case whyItMatters = "why_it_matters"
        case confidence
        case proposedBy = "proposed_by"
        case status
        case createdAt = "created_at"
        case evidenceRefs = "evidence_refs"
        case mergeSuggestion = "merge_suggestion"
        case conflictSuggestion = "conflict_suggestion"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        candidateID = (try? c.decode(String.self, forKey: .candidateID)) ?? UUID().uuidString
        kind = (try? c.decode(String.self, forKey: .kind)) ?? ""
        summary = (try? c.decode(String.self, forKey: .summary)) ?? ""
        value = (try? c.decode(String.self, forKey: .value)) ?? ""
        whyItMatters = (try? c.decode(String.self, forKey: .whyItMatters)) ?? ""
        confidence = (try? c.decode(Double.self, forKey: .confidence)) ?? 0
        proposedBy = (try? c.decode(String.self, forKey: .proposedBy)) ?? ""
        status = (try? c.decode(String.self, forKey: .status)) ?? ""
        createdAt = (try? c.decode(String.self, forKey: .createdAt)) ?? ""
        evidenceRefs = (try? c.decodeIfPresent([EvidenceRef].self, forKey: .evidenceRefs)) ?? []
        mergeSuggestion = try? c.decodeIfPresent(JSONValue.self, forKey: .mergeSuggestion)
        conflictSuggestion = try? c.decodeIfPresent(JSONValue.self, forKey: .conflictSuggestion)
    }
}

/// An approved startup rule, from `list_repo_memories`.
struct ApprovedMemory: Identifiable, Hashable, Codable {
    let memoryID: String
    let kind: String
    let title: String
    let value: String
    let usageHint: String
    let status: String
    let lastVerifiedAt: String?
    let freshnessStatus: String
    let freshnessScore: Double

    var id: String { memoryID }

    var freshnessLabel: String {
        switch freshnessStatus.lowercased() {
        case "fresh": "有效"
        case "needs_review", "stale": "需复核"
        case "unknown": "未知"
        default: freshnessStatus
        }
    }

    var kindLabel: String {
        switch kind.lowercased() {
        case "command": "命令"
        case "convention": "约定"
        case "decision": "决策"
        case "gotcha": "注意事项"
        case "preference": "偏好"
        default: kind
        }
    }

    enum CodingKeys: String, CodingKey {
        case memoryID = "memory_id"
        case kind, title, value
        case usageHint = "usage_hint"
        case status
        case lastVerifiedAt = "last_verified_at"
        case freshnessStatus = "freshness_status"
        case freshnessScore = "freshness_score"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        memoryID = (try? c.decode(String.self, forKey: .memoryID)) ?? UUID().uuidString
        kind = (try? c.decode(String.self, forKey: .kind)) ?? ""
        title = (try? c.decode(String.self, forKey: .title)) ?? ""
        value = (try? c.decode(String.self, forKey: .value)) ?? ""
        usageHint = (try? c.decode(String.self, forKey: .usageHint)) ?? ""
        status = (try? c.decode(String.self, forKey: .status)) ?? ""
        lastVerifiedAt = try? c.decodeIfPresent(String.self, forKey: .lastVerifiedAt)
        freshnessStatus = (try? c.decode(String.self, forKey: .freshnessStatus)) ?? "unknown"
        freshnessScore = (try? c.decode(Double.self, forKey: .freshnessScore)) ?? 0
    }
}

/// A generated wiki projection, from `list_wiki_pages`.
struct WikiPage: Identifiable, Hashable, Codable {
    let pageID: String
    let slug: String
    let title: String
    let body: String
    let status: String
    let lastBuiltAt: String?

    var id: String { pageID }

    enum CodingKeys: String, CodingKey {
        case pageID = "page_id"
        case slug, title, body, status
        case lastBuiltAt = "last_built_at"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        pageID = (try? c.decode(String.self, forKey: .pageID)) ?? UUID().uuidString
        slug = (try? c.decode(String.self, forKey: .slug)) ?? ""
        title = (try? c.decode(String.self, forKey: .title)) ?? ""
        body = (try? c.decode(String.self, forKey: .body)) ?? ""
        status = (try? c.decode(String.self, forKey: .status)) ?? ""
        lastBuiltAt = try? c.decodeIfPresent(String.self, forKey: .lastBuiltAt)
    }
}

/// A frozen context checkpoint, from `list_checkpoints`.
struct Checkpoint: Identifiable, Hashable, Codable {
    let checkpointID: String
    let repoRoot: String?
    let conversationID: String
    let sourceAgent: String
    let status: String
    let summary: String
    let resumeCommand: String?
    let metadataJSON: String
    let handoffID: String?
    let createdAt: String

    var id: String { checkpointID }
    var agentKind: AgentKind? { AgentKind(rawValue: sourceAgent) }

    /// Parsed metadata (best-effort).
    var metadata: [String: JSONValue] {
        guard let data = metadataJSON.data(using: .utf8),
              let obj = try? JSONDecoder().decode([String: JSONValue].self, from: data)
        else { return [:] }
        return obj
    }

    var messageCount: Int? {
        if case .number(let n) = metadata["message_count"] { return Int(n) }
        return nil
    }

    enum CodingKeys: String, CodingKey {
        case checkpointID = "checkpoint_id"
        case repoRoot = "repo_root"
        case conversationID = "conversation_id"
        case sourceAgent = "source_agent"
        case status, summary
        case resumeCommand = "resume_command"
        case metadataJSON = "metadata_json"
        case handoffID = "handoff_id"
        case createdAt = "created_at"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        checkpointID = (try? c.decode(String.self, forKey: .checkpointID)) ?? UUID().uuidString
        repoRoot = try? c.decodeIfPresent(String.self, forKey: .repoRoot)
        conversationID = (try? c.decode(String.self, forKey: .conversationID)) ?? ""
        sourceAgent = (try? c.decode(String.self, forKey: .sourceAgent)) ?? ""
        status = (try? c.decode(String.self, forKey: .status)) ?? ""
        summary = (try? c.decode(String.self, forKey: .summary)) ?? ""
        resumeCommand = try? c.decodeIfPresent(String.self, forKey: .resumeCommand)
        metadataJSON = (try? c.decode(String.self, forKey: .metadataJSON)) ?? "{}"
        handoffID = try? c.decodeIfPresent(String.self, forKey: .handoffID)
        createdAt = (try? c.decode(String.self, forKey: .createdAt)) ?? ""
    }
}

/// A cross-agent handoff packet, from `list_handoffs`.
struct HandoffPacket: Identifiable, Hashable, Codable {
    let handoffID: String
    let repoRoot: String?
    let fromAgent: String
    let toAgent: String
    let status: String
    let checkpointID: String?
    let targetProfile: String?
    let currentGoal: String
    let doneItems: [String]
    let nextItems: [String]
    let keyFiles: [String]
    let usefulCommands: [String]
    let createdAt: String?

    var id: String { handoffID }

    enum CodingKeys: String, CodingKey {
        case handoffID = "handoff_id"
        case repoRoot = "repo_root"
        case fromAgent = "from_agent"
        case toAgent = "to_agent"
        case status
        case checkpointID = "checkpoint_id"
        case targetProfile = "target_profile"
        case currentGoal = "current_goal"
        case doneItems = "done_items"
        case nextItems = "next_items"
        case keyFiles = "key_files"
        case usefulCommands = "useful_commands"
        case createdAt = "created_at"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        handoffID = (try? c.decode(String.self, forKey: .handoffID)) ?? UUID().uuidString
        repoRoot = try? c.decodeIfPresent(String.self, forKey: .repoRoot)
        fromAgent = (try? c.decode(String.self, forKey: .fromAgent)) ?? ""
        toAgent = (try? c.decode(String.self, forKey: .toAgent)) ?? ""
        status = (try? c.decode(String.self, forKey: .status)) ?? ""
        checkpointID = try? c.decodeIfPresent(String.self, forKey: .checkpointID)
        targetProfile = try? c.decodeIfPresent(String.self, forKey: .targetProfile)
        currentGoal = (try? c.decode(String.self, forKey: .currentGoal)) ?? ""
        doneItems = (try? c.decodeIfPresent([String].self, forKey: .doneItems)) ?? []
        nextItems = (try? c.decodeIfPresent([String].self, forKey: .nextItems)) ?? []
        keyFiles = (try? c.decodeIfPresent([String].self, forKey: .keyFiles)) ?? []
        usefulCommands = (try? c.decodeIfPresent([String].self, forKey: .usefulCommands)) ?? []
        createdAt = try? c.decodeIfPresent(String.self, forKey: .createdAt)
    }
}

struct AgentRunRecord: Identifiable, Hashable, Codable, Sendable {
    let runID: String
    let repoRoot: String
    let sourceAgent: String
    let taskHint: String?
    let status: String
    let summary: String
    let startedAt: String
    let endedAt: String?
    let artifactCount: Int

    var id: String { runID }

    enum CodingKeys: String, CodingKey {
        case runID = "run_id"
        case repoRoot = "repo_root"
        case sourceAgent = "source_agent"
        case taskHint = "task_hint"
        case status, summary
        case startedAt = "started_at"
        case endedAt = "ended_at"
        case artifactCount = "artifact_count"
    }
}

struct RunArtifactRecord: Identifiable, Hashable, Codable, Sendable {
    let artifactID: String
    let runID: String
    let artifactType: String
    let title: String
    let summary: String
    let trustState: String
    let createdAt: String

    var id: String { artifactID }

    enum CodingKeys: String, CodingKey {
        case artifactID = "artifact_id"
        case runID = "run_id"
        case artifactType = "artifact_type"
        case title, summary
        case trustState = "trust_state"
        case createdAt = "created_at"
    }
}

struct MemoryConflictRecord: Identifiable, Hashable, Codable, Sendable {
    let conflictID: String
    let candidateID: String
    let memoryID: String
    let memoryTitle: String
    let reason: String
    let status: String
    let createdAt: String

    var id: String { conflictID }

    enum CodingKeys: String, CodingKey {
        case conflictID = "conflict_id"
        case candidateID = "candidate_id"
        case memoryID = "memory_id"
        case memoryTitle = "memory_title"
        case reason, status
        case createdAt = "created_at"
    }
}

struct MemoryEntityNode: Identifiable, Hashable, Codable, Sendable {
    let entityID: String
    let name: String
    let kind: String
    let mentionCount: Int

    var id: String { entityID }

    enum CodingKeys: String, CodingKey {
        case entityID = "entity_id"
        case name, kind
        case mentionCount = "mention_count"
    }
}

struct MemoryEntityLink: Identifiable, Hashable, Codable, Sendable {
    let entityID: String
    let entityName: String
    let ownerType: String
    let ownerID: String
    let relationship: String
    let sourceTitle: String
    let sourceConversationID: String?

    var id: String { "\(entityID):\(ownerType):\(ownerID):\(relationship)" }

    enum CodingKeys: String, CodingKey {
        case entityID = "entity_id"
        case entityName = "entity_name"
        case ownerType = "owner_type"
        case ownerID = "owner_id"
        case relationship
        case sourceTitle = "source_title"
        case sourceConversationID = "source_conversation_id"
    }
}

struct MemoryEntityGraph: Hashable, Codable, Sendable {
    let entities: [MemoryEntityNode]
    let links: [MemoryEntityLink]
}

struct EpisodeRecord: Identifiable, Hashable, Codable, Sendable {
    let episodeID: String
    let title: String
    let summary: String
    let outcome: String
    let createdAt: String
    let sourceConversationID: String

    var id: String { episodeID }

    enum CodingKeys: String, CodingKey {
        case episodeID = "episode_id"
        case title, summary, outcome
        case createdAt = "created_at"
        case sourceConversationID = "source_conversation_id"
    }
}

/// A restorable trash record, from `list_trashed_conversations`.
struct TrashRecord: Identifiable, Hashable, Decodable {
    let trashID: String
    let originalID: String
    let sourceAgent: String
    let projectDir: String
    let summary: String?
    let trashedAt: String
    let expiresAt: String
    let resumeCommand: String?
    let storagePath: String?
    let warnings: [String]
    let recordPath: String

    var id: String { trashID }
    var agentKind: AgentKind? { AgentKind(rawValue: sourceAgent) }

    enum CodingKeys: String, CodingKey {
        case trashID = "trash_id"
        case originalID = "original_id"
        case sourceAgent = "source_agent"
        case projectDir = "project_dir"
        case summary
        case trashedAt = "trashed_at"
        case expiresAt = "expires_at"
        case resumeCommand = "resume_command"
        case storagePath = "storage_path"
        case warnings
        case recordPath = "record_path"
        case camelTrashID = "trashId"
        case camelOriginalID = "originalId"
        case camelSourceAgent = "sourceAgent"
        case camelProjectDir = "projectDir"
        case camelTrashedAt = "trashedAt"
        case camelExpiresAt = "expiresAt"
        case camelResumeCommand = "resumeCommand"
        case camelStoragePath = "storagePath"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        trashID = (try? c.decode(String.self, forKey: .trashID))
            ?? (try? c.decode(String.self, forKey: .camelTrashID))
            ?? UUID().uuidString
        originalID = (try? c.decode(String.self, forKey: .originalID))
            ?? (try? c.decode(String.self, forKey: .camelOriginalID))
            ?? ""
        sourceAgent = (try? c.decode(String.self, forKey: .sourceAgent))
            ?? (try? c.decode(String.self, forKey: .camelSourceAgent))
            ?? ""
        projectDir = (try? c.decode(String.self, forKey: .projectDir))
            ?? (try? c.decode(String.self, forKey: .camelProjectDir))
            ?? ""
        summary = try? c.decodeIfPresent(String.self, forKey: .summary)
        trashedAt = (try? c.decode(String.self, forKey: .trashedAt))
            ?? (try? c.decode(String.self, forKey: .camelTrashedAt))
            ?? ""
        expiresAt = (try? c.decode(String.self, forKey: .expiresAt))
            ?? (try? c.decode(String.self, forKey: .camelExpiresAt))
            ?? ""
        resumeCommand = (try? c.decodeIfPresent(String.self, forKey: .resumeCommand))
            ?? (try? c.decodeIfPresent(String.self, forKey: .camelResumeCommand))
        storagePath = (try? c.decodeIfPresent(String.self, forKey: .storagePath))
            ?? (try? c.decodeIfPresent(String.self, forKey: .camelStoragePath))
        warnings = (try? c.decodeIfPresent([String].self, forKey: .warnings)) ?? []
        recordPath = (try? c.decode(String.self, forKey: .recordPath)) ?? ""
    }
}

/// A repo with its pending-candidate count, from `list_repos_with_candidates`.
struct RepoCandidateCount: Identifiable, Hashable, Codable {
    let repoRoot: String
    let pendingCount: Int
    var id: String { repoRoot }

    enum CodingKeys: String, CodingKey {
        case repoRoot = "repo_root"
        case pendingCount = "pending_count"
    }
}

/// Result of `detect_agent_integrations`: per-agent install status.
struct AgentIntegrationStatus: Identifiable, Hashable, Codable, Sendable {
    let agent: String
    let label: String
    let configPath: String?
    let instructionsPath: String?
    let mcpInstalled: Bool
    let instructionsInstalled: Bool
    let configExists: Bool
    let agentDetected: Bool?
    let integrationAvailable: Bool?
    let status: String
    let statusLabel: String
    let commandPreview: String?
    let details: [String]

    var id: String { agent }
    var agentKind: AgentKind? { AgentKind(rawValue: agent) }

    init(
        agent: String,
        label: String,
        configPath: String?,
        instructionsPath: String?,
        mcpInstalled: Bool,
        instructionsInstalled: Bool,
        configExists: Bool,
        agentDetected: Bool? = nil,
        integrationAvailable: Bool? = nil,
        status: String,
        statusLabel: String,
        commandPreview: String?,
        details: [String]
    ) {
        self.agent = agent
        self.label = label
        self.configPath = configPath
        self.instructionsPath = instructionsPath
        self.mcpInstalled = mcpInstalled
        self.instructionsInstalled = instructionsInstalled
        self.configExists = configExists
        self.agentDetected = agentDetected
        self.integrationAvailable = integrationAvailable
        self.status = status
        self.statusLabel = statusLabel
        self.commandPreview = commandPreview
        self.details = details
    }

    enum CodingKeys: String, CodingKey {
        case agent, label
        case configPath = "configPath"
        case instructionsPath = "instructionsPath"
        case mcpInstalled = "mcpInstalled"
        case instructionsInstalled = "instructionsInstalled"
        case configExists = "configExists"
        case agentDetected = "agentDetected"
        case integrationAvailable = "integrationAvailable"
        case status
        case statusLabel = "statusLabel"
        case commandPreview = "commandPreview"
        case details
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        agent = (try? c.decode(String.self, forKey: .agent)) ?? ""
        label = (try? c.decode(String.self, forKey: .label)) ?? ""
        configPath = try? c.decodeIfPresent(String.self, forKey: .configPath)
        instructionsPath = try? c.decodeIfPresent(String.self, forKey: .instructionsPath)
        mcpInstalled = (try? c.decode(Bool.self, forKey: .mcpInstalled)) ?? false
        instructionsInstalled = (try? c.decode(Bool.self, forKey: .instructionsInstalled)) ?? false
        configExists = (try? c.decode(Bool.self, forKey: .configExists)) ?? false
        agentDetected = try? c.decodeIfPresent(Bool.self, forKey: .agentDetected)
        integrationAvailable = try? c.decodeIfPresent(
            Bool.self,
            forKey: .integrationAvailable
        )
        status = (try? c.decode(String.self, forKey: .status)) ?? "unknown"
        statusLabel = (try? c.decode(String.self, forKey: .statusLabel)) ?? ""
        commandPreview = try? c.decodeIfPresent(String.self, forKey: .commandPreview)
        details = (try? c.decodeIfPresent([String].self, forKey: .details)) ?? []
    }

    var isAgentDetected: Bool { agentDetected ?? configExists }
    var canInstallIntegration: Bool { integrationAvailable ?? true }
}

/// Result of `trash_conversation` / `restore_trashed_conversation`.
struct TrashActionResult: Hashable, Codable {
    let trashID: String
    let originalID: String?
    let restoredID: String?
    let sourceAgent: String?
    let warnings: [String]

    enum CodingKeys: String, CodingKey {
        case trashID = "trash_id"
        case originalID = "original_id"
        case restoredID = "restored_id"
        case sourceAgent = "source_agent"
        case warnings
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        trashID = (try? c.decode(String.self, forKey: .trashID)) ?? UUID().uuidString
        originalID = try? c.decodeIfPresent(String.self, forKey: .originalID)
        restoredID = try? c.decodeIfPresent(String.self, forKey: .restoredID)
        sourceAgent = try? c.decodeIfPresent(String.self, forKey: .sourceAgent)
        warnings = (try? c.decodeIfPresent([String].self, forKey: .warnings)) ?? []
    }
}

/// Result of `migrate_conversation`.
struct MigrationResult: Hashable, Codable {
    let newID: String
    let source: String
    let target: String
    let mode: String
    let verified: Bool
    let sourceMessageCount: Int
    let targetMessageCount: Int
    let sourceFileCount: Int
    let firstUserPreserved: Bool
    let cutDeletedSource: Bool
    let warnings: [String]

    enum CodingKeys: String, CodingKey {
        case newID = "new_id"
        case source, target, mode, verified
        case sourceMessageCount = "source_message_count"
        case targetMessageCount = "target_message_count"
        case sourceFileCount = "source_file_count"
        case firstUserPreserved = "first_user_preserved"
        case cutDeletedSource = "cut_deleted_source"
        case warnings
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        newID = (try? c.decode(String.self, forKey: .newID)) ?? ""
        source = (try? c.decode(String.self, forKey: .source)) ?? ""
        target = (try? c.decode(String.self, forKey: .target)) ?? ""
        mode = (try? c.decode(String.self, forKey: .mode)) ?? "copy"
        verified = (try? c.decode(Bool.self, forKey: .verified)) ?? false
        sourceMessageCount = (try? c.decode(Int.self, forKey: .sourceMessageCount)) ?? 0
        targetMessageCount = (try? c.decode(Int.self, forKey: .targetMessageCount)) ?? 0
        sourceFileCount = (try? c.decode(Int.self, forKey: .sourceFileCount)) ?? 0
        firstUserPreserved = (try? c.decode(Bool.self, forKey: .firstUserPreserved)) ?? false
        cutDeletedSource = (try? c.decode(Bool.self, forKey: .cutDeletedSource)) ?? false
        warnings = (try? c.decodeIfPresent([String].self, forKey: .warnings)) ?? []
    }
}

// MARK: - Sidebar grouping models
// Mirrors ChatMem's ProjectGroup interface and machineGroups memo.

/// A group of conversations sharing the same project directory.
/// Mirrors ChatMem's `ProjectGroup` interface (App.tsx ~3233).
struct ProjectGroup: Identifiable, Hashable {
    let id: String           // groupKey (projectPathKey or zcode-cli:projectKey)
    let label: String        // project leaf name (getProjectLabel)
    let fullPath: String     // normalized project_dir
    var latestAt: String     // most recent updated_at in the group (ISO 8601)
    var conversations: [ConversationSummary]

    /// Aggregate message count across all conversations in this group.
    var totalMessages: Int { conversations.reduce(0) { $0 + $1.messageCount } }
    /// Aggregate file-change count across all conversations.
    var totalFiles: Int { conversations.reduce(0) { $0 + $1.fileCount } }
}

/// A machine/platform group containing one or more project groups.
/// Mirrors ChatMem's `machineGroups` memo (App.tsx ~3310).
struct MachineGroup: Identifiable, Hashable {
    let id: String           // "windows" / "macos" / "linux" / "internal" / "other"
    var label: String        // "Windows" / "Mac" / auto: "Windows-1" / "Mac-2"
    var latestAt: String
    var projects: [ProjectGroup]

    var conversationCount: Int { projects.reduce(0) { $0 + $1.conversations.count } }
}

/// Arrangement mode for the sidebar list.
enum ArrangeMode: String, CaseIterable, Hashable {
    case byProject    // 按项目
    case timeline     // 时间线
    case chatsFirst   // 对话优先
}

/// Sort mode for conversations.
enum SortMode: String, CaseIterable, Hashable {
    case updatedDesc  // 最近更新
    case createdDesc  // 最近创建
    case titleAsc     // 标题
}
