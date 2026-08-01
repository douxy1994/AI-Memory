// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import Foundation

enum ContinuationBriefBuilder {
    private struct Candidate {
        let role: String
        let content: String
        let index: Int
    }

    private static let completedWords = [
        "已经", "已完成", "已生成", "已更新", "已同步", "已删除", "已处理",
        "已复制", "已迁移", "已修复", "已提交", "完成", "implemented",
        "completed", "updated", "fixed", "done", "created", "copied", "committed",
    ]
    private static let obsoleteWords = [
        "拒稿", "归档", "历史", "旧版", "过时", "不用", "删除", "EI",
        "obsolete", "archived", "superseded", "deprecated",
    ]
    private static let processNoise = [
        "我会先", "我先", "先看", "先定位", "接下来", "`rg`", "路径确认",
        "完整性核对", "I will first", "I'll first",
    ]
    private static let filePattern =
        #"(?i)(?:[A-Z]:)?(?:[/\\][^\s`，。；;:]+)+\.(?:md|docx|xlsx|csv|py|r|tsx?|jsx?|swift|png|pdf|svg|tiff?|drawio)"#

    static func build(
        repoRoot: String,
        conversation: ConversationDetail,
        checkpointID: String? = nil,
        handoffID: String? = nil
    ) -> String {
        let candidates = conversation.messages.enumerated().compactMap { index, message -> Candidate? in
            let content = clean(message.content)
            guard !content.isEmpty, !isSystemMessage(content) else { return nil }
            return Candidate(role: message.role, content: content, index: index)
        }
        let latestUser = candidates.last {
            $0.role.lowercased() == "user" && !isNoise($0.content)
        }
        let latestCompleted = candidates.last {
            $0.role.lowercased() == "assistant"
                && !isNoise($0.content)
                && containsAny($0.content, completedWords)
        }
        let fallbackSummary = clean(conversation.summary ?? "")
        let currentGoal: String
        if let latestUser, let latestCompleted, latestCompleted.index > latestUser.index {
            currentGoal = "最新请求已处理完成"
        } else {
            currentGoal = latestUser?.content
                ?? nonempty(fallbackSummary)
                ?? "Continue from the latest available project context."
        }
        let currentWorkline = latestUser?.content
            ?? nonempty(fallbackSummary)
            ?? "Use the source-backed conversation to recover the active workline."
        let latestAction = latestCompleted?.content
            ?? candidates.last(where: { $0.role.lowercased() == "assistant" })?.content
            ?? "No completed assistant action was detected."
        let whereToResume = latestUser?.content
            ?? nonempty(fallbackSummary)
            ?? "Continue from the latest available project context."

        var canonicalFiles = conversation.fileChanges.map(\.path)
        for candidate in candidates.suffix(12) {
            canonicalFiles.append(contentsOf: extractFilePaths(candidate.content))
        }
        canonicalFiles = unique(canonicalFiles).filter {
            !$0.localizedCaseInsensitiveContains("codex-clipboard")
                && !$0.localizedCaseInsensitiveContains("/temp/")
        }

        let obsolete = candidates
            .filter {
                $0.index != latestUser?.index
                    && $0.index != latestCompleted?.index
                    && containsAny($0.content, obsoleteWords)
                    && !isNoise($0.content)
            }
            .suffix(3)
            .map { "\($0.role): \(truncate($0.content))" }
        let evidence = candidates
            .filter { !isNoise($0.content) }
            .suffix(3)
            .map { "\($0.role): \(truncate($0.content))" }
        let source = "\(conversation.sourceAgent):\(conversation.id)"
        let rawTokenEstimate = max(
            1,
            Int(ceil(Double(conversation.messages.map(\.content).joined(separator: "\n").count) / 4.0))
        )

        var lines = [
            "# Continuation Brief",
            "",
            "Use ChatMem to continue this project from a compact, source-backed brief.",
            "Treat the original conversation as evidence to inspect on demand, not as startup context.",
            "",
            "## Scope",
            "- repo: \(repoRoot)",
            "- conversation: \(source)",
            "- source agent: \(conversation.sourceAgent)",
            "- Current goal: \(truncate(currentGoal, limit: 220))",
        ]
        if let command = nonempty(clean(conversation.resumeCommand ?? "")) {
            lines.append("- resume command: \(command)")
        }
        if let checkpointID = nonempty(checkpointID) {
            lines.append("- checkpoint: \(checkpointID)")
        }
        if let handoffID = nonempty(handoffID) {
            lines.append("- handoff: \(handoffID)")
        }
        lines += [
            "",
            "## Current workline",
            "- \(truncate(currentWorkline))",
            "",
            "## Latest completed action",
            "- \(truncate(latestAction))",
            "",
            "## Where to resume",
            "- Start from the latest user request: \(truncate(whereToResume, limit: 220))",
            "- Treat older or archived work as background unless focused evidence proves it is active again.",
            "",
            "## Canonical files",
        ]
        lines += listLines(
            canonicalFiles,
            fallback: "No file changes were captured for this conversation."
        )
        lines += [
            "",
            "## Obsolete or archived context",
        ]
        lines += listLines(
            Array(obsolete),
            fallback: "No obsolete or archived context was detected."
        )
        lines += [
            "",
            "## Evidence",
            "- Evidence source: \(source)",
        ]
        lines += listLines(
            Array(evidence),
            fallback: "Use search_repo_history before expanding the conversation."
        )
        lines += [
            "",
            "## Token posture:",
            "- Estimated raw transcript tokens: \(rawTokenEstimate)",
            "- Start from this brief instead of the raw transcript.",
            "- Open focused evidence windows only when the brief and project context are insufficient.",
            "",
            "## Continuation Protocol",
            #"1. First call get_project_context with intent="continue_work" and limit=3."#,
            "2. Prefer approved memories, recent checkpoints/handoffs, wiki, and relevant_history summaries.",
            "3. If evidence is missing, call search_repo_history with limit<=3.",
            "4. Read the original conversation only through read_history_conversation for a focused window.",
            "5. Do not replay the full transcript or tool logs unless the focused evidence is insufficient.",
        ]
        return lines.joined(separator: "\n")
    }

    private static func clean(_ value: String) -> String {
        var result = value
        if let marker = result.range(
            of: #"##\s*My request for Codex:\s*"#,
            options: [.regularExpression, .caseInsensitive]
        ) {
            result = String(result[marker.upperBound...])
        }
        result = result.replacingOccurrences(
            of: #"<image\b[\s\S]*?</image>"#,
            with: "",
            options: [.regularExpression, .caseInsensitive]
        )
        return result.replacingOccurrences(
            of: #"\s+"#,
            with: " ",
            options: .regularExpression
        ).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func isSystemMessage(_ content: String) -> Bool {
        [
            "<environment_context>", "<permissions instructions>", "<apps_instructions>",
            "<skills_instructions>", "<plugins_instructions>", "<collaboration_mode>",
            "# AGENTS.md instructions",
        ].contains { content.hasPrefix($0) }
    }

    private static func containsAny(_ value: String, _ words: [String]) -> Bool {
        words.contains { value.localizedCaseInsensitiveContains($0) }
    }

    private static func isNoise(_ content: String) -> Bool {
        containsAny(content, processNoise) && !containsAny(content, completedWords)
    }

    private static func extractFilePaths(_ content: String) -> [String] {
        guard let regex = try? NSRegularExpression(pattern: filePattern) else { return [] }
        let range = NSRange(content.startIndex..., in: content)
        return regex.matches(in: content, range: range).compactMap {
            Range($0.range, in: content).map { String(content[$0]) }
        }
    }

    private static func unique(_ values: [String]) -> [String] {
        var seen = Set<String>()
        return values.filter {
            let key = $0.replacingOccurrences(of: "\\", with: "/").lowercased()
            return !$0.isEmpty && seen.insert(key).inserted
        }
    }

    private static func listLines(_ values: [String], fallback: String) -> [String] {
        values.isEmpty ? ["- \(fallback)"] : values.map { "- \($0)" }
    }

    private static func truncate(_ value: String, limit: Int = 180) -> String {
        guard value.count > limit else { return value }
        return String(value.prefix(max(0, limit - 3))) + "..."
    }

    private static func nonempty(_ value: String?) -> String? {
        guard let value, !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return nil
        }
        return value
    }
}
