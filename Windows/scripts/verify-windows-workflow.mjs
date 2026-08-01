// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "../..");
const workflowPath = path.join(root, ".github", "workflows", "build.yml");
const smokePath = path.join(root, "Windows", "scripts", "smoke-desktop.ps1");
const appPath = path.join(
  root,
  "Windows",
  "src",
  "AIMemory.Windows",
  "App.xaml.cs",
);
const mainWindowPath = path.join(
  root,
  "Windows",
  "src",
  "AIMemory.Windows",
  "MainWindow.xaml.cs",
);
const workflow = fs.readFileSync(workflowPath, "utf8");
const smoke = fs.readFileSync(smokePath, "utf8");
const app = fs.readFileSync(appPath, "utf8");
const mainWindow = fs.readFileSync(mainWindowPath, "utf8");

const workflowRequirements = [
  ["Windows 11 runner", "runs-on: windows-2025"],
  ["x64 build", "Build WinUI 3 x64"],
  ["x64 platform", "-p:Platform=x64"],
  ["ARM64 build", "Build WinUI 3 ARM64"],
  ["ARM64 platform", "-p:Platform=ARM64"],
  ["MCP helper smoke", "Smoke test packaged MCP helper"],
  ["MCP helper payload", "aimemory-mcp.exe"],
  ["WinUI code-behind contract", "Verify WinUI XAML and code-behind contracts"],
  ["code-behind verifier", "Windows/scripts/verify-codebehind.mjs"],
  ["unsigned MSIX", "GenerateAppxPackageOnBuild=true"],
  ["unsigned package mode", "AppxPackageSigningEnabled=false"],
  ["generated package manifest", "AppxManifest.xml"],
  ["package registration", "Add-AppxPackage -Path $manifest.FullName -Register -DisableDevelopmentMode"],
  ["package identity", "-AppUserModelId $appUserModelId"],
  ["manifest identity guard", "Identity\\s+Name=\"com\\.aimemory\\.windows\""],
  ["manifest token guard", "still contains unresolved build tokens"],
  ["manifest executable guard", "AIMemory\\.Windows\\.exe"],
  ["desktop smoke script", "smoke-desktop.ps1"],
  ["startup diagnostics", "AIMemory-startup.log"],
  ["Windows build artifact upload", "windows-build-artifacts"],
  ["MSIX artifact path", "Windows/src/AIMemory.Windows/bin/**/*.msix"],
];

for (const [label, value] of workflowRequirements) {
  if (!workflow.includes(value)) {
    throw new Error(`Windows workflow contract is missing ${label}: ${value}`);
  }
}

if (workflow.includes("-ExecutablePath $app.FullName")) {
  throw new Error(
    "The Windows workflow must launch the registered package, not a raw executable.",
  );
}

const smokeRequirements = [
  ["interactive session guard", "[Environment]::UserInteractive"],
  ["Explorer guard", "Get-Process explorer"],
  ["package launch mode", "shell:AppsFolder\\$AppUserModelId"],
  ["single process assertion", "Count -eq 1"],
  ["visible window assertion", "IsWindowVisible"],
  ["window title assertion", "GetWindowTitle"],
  ["AI Memory title check", "*AI Memory*"],
  ["startup completion assertion", "launch.complete"],
  ["close-to-tray assertion", "CloseMainWindow"],
  ["relaunch assertion", "RelaunchRestoresWindow"],
];

for (const [label, value] of smokeRequirements) {
  if (!smoke.includes(value)) {
    throw new Error(`Desktop smoke contract is missing ${label}: ${value}`);
  }
}

const diagnosticsRequirements = [
  ["window activation diagnostic", "window.activate.called"],
  ["window show diagnostic", "window.bring-to-front.completed"],
  ["notification-area success diagnostic", "notification-area.ready"],
  ["notification-area failure diagnostic", "notification-area.failed"],
];

for (const [label, value] of diagnosticsRequirements.slice(0, 2)) {
  if (!app.includes(value)) {
    throw new Error(`Startup diagnostics are missing ${label}: ${value}`);
  }
}
for (const [label, value] of diagnosticsRequirements.slice(2)) {
  if (!mainWindow.includes(value)) {
    throw new Error(`Startup diagnostics are missing ${label}: ${value}`);
  }
}

console.log(
  "Windows workflow contract verified: x64/ARM64, unsigned MSIX, packaged MCP, " +
    "AppsFolder registration, startup diagnostics, single-instance desktop smoke, " +
    "and WinUI code-behind contract verification.",
);
