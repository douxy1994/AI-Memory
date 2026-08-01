// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "..");
const manifestPath = path.join(
  root,
  "src",
  "AIMemory.Windows",
  "Package.appxmanifest",
);
const projectPath = path.join(
  root,
  "src",
  "AIMemory.Windows",
  "AIMemory.Windows.csproj",
);
const assetsRoot = path.join(root, "src", "AIMemory.Windows", "Assets");
const manifest = fs.readFileSync(manifestPath, "utf8");
const project = fs.readFileSync(projectPath, "utf8");

const requiredManifestFragments = [
  '<Identity Name="com.aimemory.windows"',
  'Publisher="CN=AI Memory"',
  '<TargetDeviceFamily',
  'Name="Windows.Desktop"',
  '<Application',
  'Id="App"',
  'Executable="$targetnametoken$.exe"',
  'EntryPoint="$targetentrypoint$"',
  'Square44x44Logo="Assets\\Square44x44Logo.png"',
  'Square150x150Logo="Assets\\Square150x150Logo.png"',
  'Category="windows.startupTask"',
  'TaskId="AIMemoryStartup"',
  'Enabled="false"',
  '<Resource Language="zh-CN"',
  '<Resource Language="en-US"',
  '<Capability Name="internetClient"',
  '<rescap:Capability Name="runFullTrust"',
];
for (const fragment of requiredManifestFragments) {
  if (!manifest.includes(fragment)) {
    throw new Error(`Package manifest is missing: ${fragment}`);
  }
}

for (const required of [
  "<WindowsPackageType>MSIX</WindowsPackageType>",
  "<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>",
  "<Platforms>x64;ARM64</Platforms>",
  "<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>",
]) {
  if (!project.includes(required)) {
    throw new Error(`Windows project packaging setting is missing: ${required}`);
  }
}

for (const asset of [
  "AppIcon.ico",
  "AppIcon.png",
  "Square44x44Logo.png",
  "Square150x150Logo.png",
  "StoreLogo.png",
]) {
  const file = path.join(assetsRoot, asset);
  if (!fs.existsSync(file) || fs.statSync(file).size === 0) {
    throw new Error(`Windows package asset is missing or empty: ${asset}`);
  }
}

console.log(
  "Windows package manifest, MSIX settings, startup task, resources, and icon assets verified.",
);
