// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
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
const workflowPath = path.join(root, ".github", "workflows", "build.yml");
const readmePaths = [
  [path.join(root, "README.md"), "种 Agent"],
  [path.join(root, "Windows", "README.md"), "种主流 Agent"],
  [path.join(root, "docs", "FEATURE_MATRIX.md"), "种 Agent/CLI"],
];
const windowsReadmePath = path.join(root, "Windows", "README.md");

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
  /static let all: \[DetectionOnlyAgent\] = \[([\s\S]*?)\r?\n\s*\]\r?\n\}/,
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

const workflow = fs.readFileSync(workflowPath, "utf8");
for (const required of [
  "AppxManifest.xml",
  "ExtractToDirectory($msix.FullName, $layout)",
  "Add-AppxPackage -Path $manifest.FullName -Register",
  "-AppUserModelId $appUserModelId",
  "smoke-desktop.ps1",
]) {
  if (!workflow.includes(required)) {
    throw new Error(`Windows desktop smoke contract is missing: ${required}`);
  }
}
if (workflow.includes("-ExecutablePath $app.FullName")) {
  throw new Error(
    "Windows desktop smoke must launch the registered package, not a raw executable.",
  );
}

const catalogCount = swiftIds.length;
for (const [file, marker] of readmePaths) {
  const text = fs.readFileSync(file, "utf8");
  if (!text.includes(`${catalogCount} ${marker}`)) {
    throw new Error(`Catalog count is stale in ${path.relative(root, file)}.`);
  }
}
const parity = fs.readFileSync(path.join(root, "Windows", "parity.json"), "utf8");
if (!parity.includes(`\"${catalogCount} products`)) {
  throw new Error("Catalog count is stale in Windows/parity.json.");
}
const windowsReadme = fs.readFileSync(windowsReadmePath, "utf8");
for (const required of [
  "ExtractToDirectory($msix.FullName, $layout)",
  "Add-AppxPackage -Path (Join-Path $layout \"AppxManifest.xml\") -Register",
  "-AppUserModelId \"$($package.PackageFamilyName)!App\"",
]) {
  if (!windowsReadme.includes(required)) {
    throw new Error(`Windows desktop smoke documentation is missing: ${required}`);
  }
}
if (windowsReadme.includes("-ExecutablePath $app.FullName")) {
  throw new Error("Windows README still documents raw executable smoke.");
}

console.log(
  `Agent catalog parity verified: ${catalogCount} IDs on macOS and Windows; ` +
    "docs and Windows package smoke contract are aligned.",
);
