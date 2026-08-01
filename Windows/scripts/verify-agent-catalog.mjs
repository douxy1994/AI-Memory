import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const root = path.resolve(import.meta.dirname, "../..");
const swiftPath = path.join(
  root,
  "AIMemory",
  "Services",
  "NativeAgentIntegrationStore.swift",
);
const csharpPath = path.join(
  root,
  "Windows",
  "src",
  "AIMemory.Core",
  "Services",
  "AgentCatalog.cs",
);

const swift = fs.readFileSync(swiftPath, "utf8");
const csharp = fs.readFileSync(csharpPath, "utf8");

const enumBody = swift.match(
  /private enum IntegrationAgent: String, CaseIterable \{([\s\S]*?)\n\s*static func catalogIndex/,
)?.[1];
if (!enumBody) throw new Error("Swift IntegrationAgent catalog was not found.");

const integrationIds = [];
for (const line of enumBody.split("\n")) {
  const trimmed = line.trim();
  if (!trimmed.startsWith("case ")) continue;
  for (const item of trimmed.slice(5).split(",")) {
    const value = item.trim();
    if (!value) continue;
    const alias = value.match(/^\w+\s*=\s*"([^"]+)"$/);
    integrationIds.push(alias ? alias[1] : value);
  }
}

const detectionBody = swift.match(
  /static let all: \[DetectionOnlyAgent\] = \[([\s\S]*?)\n\s*\]\n\}/,
)?.[1];
if (!detectionBody) throw new Error("Swift detection-only catalog was not found.");
const detectionIds = [...detectionBody.matchAll(/id:\s*"([^"]+)"/g)]
  .map((match) => match[1]);
const swiftIds = [...integrationIds, ...detectionIds];

const allBody = csharp.match(
  /public static IReadOnlyList<AgentDescriptor> All \{ get; \} =\s*\[([\s\S]*?)\n\s*\];/,
)?.[1];
if (!allBody) throw new Error("Windows AgentCatalog.All was not found.");
const windowsIds = [...allBody.matchAll(/new\("([^"]+)",/g)]
  .map((match) => match[1]);

function duplicates(values) {
  const seen = new Set();
  return values.filter((value) => {
    if (seen.has(value)) return true;
    seen.add(value);
    return false;
  });
}

const swiftDuplicates = duplicates(swiftIds);
const windowsDuplicates = duplicates(windowsIds);
if (swiftDuplicates.length || windowsDuplicates.length) {
  throw new Error(
    `Duplicate Agent IDs: macOS=${swiftDuplicates.join(",")}; ` +
      `Windows=${windowsDuplicates.join(",")}`,
  );
}

const windowsSet = new Set(windowsIds);
const swiftSet = new Set(swiftIds);
const macOnly = swiftIds.filter((id) => !windowsSet.has(id));
const windowsOnly = windowsIds.filter((id) => !swiftSet.has(id));
if (macOnly.length || windowsOnly.length || swiftIds.length !== windowsIds.length) {
  throw new Error(
    `Agent catalog drift: macOS=${swiftIds.length}, Windows=${windowsIds.length}; ` +
      `macOS-only=${macOnly.join(",") || "none"}; ` +
      `Windows-only=${windowsOnly.join(",") || "none"}`,
  );
}

console.log(`Agent catalog parity verified: ${swiftIds.length} IDs on macOS and Windows.`);
