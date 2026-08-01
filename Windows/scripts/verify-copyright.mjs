import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "../..");
const notice = "Copyright © 2026 douxy1994";
const files = [
  "LICENSE",
  "NOTICE.md",
  "README.md",
  "Windows/README.md",
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

if (missing.length > 0) {
  throw new Error(
    `Copyright notice missing from: ${missing.join(", ")}`,
  );
}

console.log(
  `Copyright notice verified in ${files.length} project and UI surfaces.`,
);
