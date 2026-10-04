#!/usr/bin/env node
// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const repoRoot = resolve(import.meta.dirname, "../..");
const sourcePath = resolve(repoRoot, "Windows/src/AIMemory.Mcp/Program.cs");
const source = readFileSync(sourcePath, "utf8");
const swiftSource = readFileSync(
  resolve(repoRoot, "AIMemoryMCP/MCPMain.swift"),
  "utf8",
);

function assert(condition, message) {
  if (!condition) {
    throw new Error(`MCP contract verification failed: ${message}`);
  }
}

function toolBlock(name) {
  const marker = `"${name}"`;
  const definitionsStart = source.lastIndexOf(
    "private static readonly object[] ToolDefinitions",
  );
  const start = source.indexOf(marker, definitionsStart);
  assert(start >= 0, `missing tool definition: ${name}`);
  const next = source.indexOf("        Tool(", start + marker.length);
  return source.slice(start, next >= 0 ? next : source.length);
}

const context = toolBlock("get_project_context");
assert(
  /repo_root\s*=\s*StringSchema\(\)[\s\S]*query\s*=\s*StringSchema\(\)[\s\S]*intent\s*=\s*StringSchema\(\)[\s\S]*limit\s*=\s*LimitSchema\(\)/.test(context),
  "get_project_context properties must be repo_root/query/intent/limit",
);
assert(
  /\["repo_root", "query"\]/.test(context),
  "get_project_context must require repo_root and query",
);

const history = toolBlock("read_history_conversation");
assert(
  /repo_root\s*=\s*StringSchema\(\)[\s\S]*conversation_id\s*=\s*StringSchema\(\)[\s\S]*message_id\s*=\s*StringSchema\(\)[\s\S]*query\s*=\s*StringSchema\(\)[\s\S]*limit\s*=\s*LimitSchema\(\)/.test(history),
  "read_history_conversation properties must include message_id/query/limit",
);
assert(
  /\["repo_root", "conversation_id"\]/.test(history),
  "read_history_conversation must require repo_root and conversation_id",
);

assert(
  /var query = Required\(arguments, "query"\);[\s\S]*var intent = Optional\(arguments, "intent"\);[\s\S]*BoundedLimit\(arguments, "limit", 3\)/.test(source),
  "get_project_context must require query, echo intent, and bound limit",
);
assert(
  /ReadForMcpAsync\([\s\S]*Optional\(arguments, "message_id"\),[\s\S]*Optional\(arguments, "query"\),[\s\S]*BoundedLimit\(arguments, "limit", 12\)/.test(source),
  "read_history_conversation must pass optional filters and bounded limit",
);
assert(
  /additionalProperties = false/.test(source),
  "MCP schemas must reject undeclared properties",
);

const windowsToolNames = [
  ...source
    .slice(source.indexOf("private static readonly object[] ToolDefinitions"))
    .matchAll(/\r?\n\s*Tool\(\r?\n\s*"([^"]+)"/g),
].map((match) => match[1]);
const swiftToolNames = [
  ...swiftSource.matchAll(/Self\.tool\("([^"]+)"/g),
].map((match) => match[1]);
assert(
  JSON.stringify(windowsToolNames) === JSON.stringify(swiftToolNames),
  `macOS and Windows MCP tool order differs: ${swiftToolNames.join(",")} vs ${windowsToolNames.join(",")}`,
);
assert(
  /case "detect_agent_integrations"[\s\S]*await integrations\.detect\(\)/.test(
    swiftSource,
  ),
  "macOS MCP must expose detect_agent_integrations through the native catalog",
);
assert(
  /"detect_agent_integrations"\s*=>\s*new\s*\{[\s\S]*integrations\s*=\s*new AgentIntegrationManager\([\s\S]*\)\s*\.Detect\(\)/.test(
    source,
  ),
  "Windows MCP must return detected and installed integration state",
);

console.log(
  `MCP contract verifier: ${swiftToolNames.length} macOS/Windows tools aligned; context, history, and agent integration contracts verified.`,
);
