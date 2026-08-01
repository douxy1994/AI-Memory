// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "../..");
const notice = "Copyright © 2026 douxy1994";
const license = "AGPL-3.0-only";

const sourceExtensions = new Set([
  ".swift",
  ".cs",
  ".mjs",
  ".ps1",
  ".sh",
  ".xaml",
  ".csproj",
  ".props",
  ".sql",
  ".slnx",
  ".manifest",
  ".appxmanifest",
]);

const sourceRoots = [
  "AIMemory",
  "AIMemoryMCP",
  "AIMemoryTests",
  "Windows/src",
  "Windows/tests",
  "Windows/scripts",
  "script",
];

function collectSourceFiles(relativeDirectory) {
  const directory = path.join(root, relativeDirectory);
  if (!fs.existsSync(directory)) return [];

  const entries = fs.readdirSync(directory, { withFileTypes: true });
  return entries.flatMap((entry) => {
    if (entry.name === "bin" || entry.name === "obj") return [];
    const relativePath = path.join(relativeDirectory, entry.name);
    if (entry.isDirectory()) return collectSourceFiles(relativePath);
    return sourceExtensions.has(path.extname(entry.name))
      ? [relativePath]
      : [];
  });
}

const sourceFiles = [
  ...sourceRoots.flatMap(collectSourceFiles),
  "Windows/Directory.Build.props",
  "project.yml",
  ".github/workflows/build.yml",
  ".codex/environments/environment.toml",
  "AIMemory/AIMemory.entitlements",
  "AIMemory/Info.plist",
  "AIMemory.xcodeproj/project.pbxproj",
  "AIMemory.xcodeproj/project.xcworkspace/contents.xcworkspacedata",
  "AIMemory.xcodeproj/xcshareddata/xcschemes/AIMemory.xcscheme",
  "Windows/AIMemory.Windows.slnx",
].filter((relativePath) => fs.existsSync(path.join(root, relativePath)));

const missingSourceHeaders = sourceFiles.filter((relativePath) => {
  const contents = fs.readFileSync(path.join(root, relativePath), "utf8");
  return !contents.includes(notice) || !contents.includes(
    "SPDX-License-Identifier: AGPL-3.0-only",
  );
});
const files = [
  "LICENSE",
  "NOTICE.md",
  "README.md",
  "Windows/README.md",
  "DEVELOPMENT.md",
  "docs/ARCHITECTURE.md",
  "docs/DATA_AND_PRIVACY.md",
  "docs/FEATURE_MATRIX.md",
  "docs/ORIGIN_AND_MIGRATION.md",
  "docs/RELEASE_NOTES_0.1.0.md",
  "AIMemory/ViewControllers/AboutView.swift",
  "AIMemory/Info.plist",
  "project.yml",
  "AIMemory.xcodeproj/project.pbxproj",
  "Windows/src/AIMemory.Windows/AboutWindow.xaml",
  "Windows/src/AIMemory.Windows/Strings/zh-CN/Resources.resw",
  "Windows/src/AIMemory.Windows/Strings/en-US/Resources.resw",
  "Windows/src/AIMemory.Core/AIMemory.Core.csproj",
  "Windows/src/AIMemory.Mcp/AIMemory.Mcp.csproj",
  "Windows/src/AIMemory.Windows/AIMemory.Windows.csproj",
];

const missing = files.filter((relativePath) => {
  const file = path.join(root, relativePath);
  return !fs.readFileSync(file, "utf8").includes(notice);
});

const licenseSurfaces = [
  "LICENSE",
  "NOTICE.md",
  "README.md",
  "Windows/README.md",
  "DEVELOPMENT.md",
  "docs/ARCHITECTURE.md",
  "docs/DATA_AND_PRIVACY.md",
  "docs/FEATURE_MATRIX.md",
  "docs/ORIGIN_AND_MIGRATION.md",
  "docs/RELEASE_NOTES_0.1.0.md",
  "AIMemory/ViewControllers/AboutView.swift",
  "Windows/src/AIMemory.Windows/AboutWindow.xaml",
  "Windows/src/AIMemory.Windows/Strings/zh-CN/Resources.resw",
  "Windows/src/AIMemory.Windows/Strings/en-US/Resources.resw",
];

const missingLicense = licenseSurfaces.filter((relativePath) => {
  const file = path.join(root, relativePath);
  return !fs.readFileSync(file, "utf8").includes(license);
});

if (missing.length > 0) {
  throw new Error(
    `Copyright notice missing from: ${missing.join(", ")}`,
  );
}

if (missingLicense.length > 0) {
  throw new Error(
    `AGPL-3.0-only identifier missing from: ${missingLicense.join(", ")}`,
  );
}

if (missingSourceHeaders.length > 0) {
  throw new Error(
    `Source copyright headers missing from: ${missingSourceHeaders.join(", ")}`,
  );
}

console.log(
  `Copyright notice and AGPL identifier verified in ${files.length} project/UI surfaces and ${sourceFiles.length} first-party source/build files.`,
);
