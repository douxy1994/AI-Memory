// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const windowsRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const appRoot = path.join(windowsRoot, "src", "AIMemory.Windows");
const coreProject = path.join(
  windowsRoot,
  "src",
  "AIMemory.Core",
  "AIMemory.Core.csproj",
);
const dotnet = process.argv[2] ?? "dotnet";
const temporaryRoot = fs.mkdtempSync(
  path.join(os.tmpdir(), "aimemory-codebehind-"),
);

function filesUnder(root, suffix) {
  const result = [];
  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    if (entry.name === "bin" || entry.name === "obj") continue;
    const fullPath = path.join(root, entry.name);
    if (entry.isDirectory()) {
      result.push(...filesUnder(fullPath, suffix));
    } else if (fullPath.endsWith(suffix)) {
      result.push(fullPath);
    }
  }
  return result;
}

function xmlEscape(value) {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll('"', "&quot;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;");
}

const sourceFiles = filesUnder(appRoot, ".cs");
const compileItems = sourceFiles
  .map((file) => `    <Compile Include="${xmlEscape(file)}" />`)
  .join("\n");
const project = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.22000.0</TargetPlatformMinVersion>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <DefineConstants>DISABLE_XAML_GENERATED_MAIN</DefineConstants>
    <EnablePriGenTooling>false</EnablePriGenTooling>
    <EnableCoreMrtTooling>false</EnableCoreMrtTooling>
    <ExpandPriResources>false</ExpandPriResources>
    <EnableDefaultPriItems>false</EnableDefaultPriItems>
    <AppxGeneratePriEnabled>false</AppxGeneratePriEnabled>
    <AppxGeneratePrisForPortableLibrariesEnabled>false</AppxGeneratePrisForPortableLibrariesEnabled>
    <IncludeProjectPriFile>false</IncludeProjectPriFile>
  </PropertyGroup>
  <ItemGroup>
${compileItems}
    <Compile Include="XamlFields.g.cs" />
    <ProjectReference Include="${xmlEscape(coreProject)}" />
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.2.0" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.4654" />
  </ItemGroup>
</Project>
`;
fs.writeFileSync(
  path.join(temporaryRoot, "CodeBehindCheck.csproj"),
  project,
);

const classFields = new Map();
const contractFailures = [];
let eventHandlerCount = 0;
const eventAttributes = [
  "Click",
  "Loaded",
  "Unloaded",
  "SelectionChanged",
  "TextChanged",
  "QueryTextChanged",
  "Invoked",
  "Checked",
  "Unchecked",
  "Toggled",
  "PointerPressed",
  "KeyDown",
  "Opening",
  "Closing",
  "SizeChanged",
  "Navigated",
  "NavigationFailed",
  "Drop",
  "DragOver",
  "ContentDialogOpening",
];
for (const file of filesUnder(appRoot, ".xaml")) {
  const source = fs.readFileSync(file, "utf8");
  const className = source.match(/x:Class="([^"]+)"/)?.[1];
  if (!className) continue;
  const codeBehind = `${file}.cs`;
  if (!fs.existsSync(codeBehind)) {
    contractFailures.push(
      `${path.relative(windowsRoot, file)}: missing code-behind ${path.basename(codeBehind)}`,
    );
  } else {
    const code = fs.readFileSync(codeBehind, "utf8");
    const shortClassName = className.slice(className.lastIndexOf(".") + 1);
    if (!new RegExp(`\\b(?:partial\\s+)?class\\s+${shortClassName}\\b`).test(code)) {
      contractFailures.push(
        `${path.relative(windowsRoot, file)}: code-behind does not declare ${className}`,
      );
    }
    const attributes = eventAttributes.join("|");
    for (const match of source.matchAll(
      new RegExp(`\\b(?:${attributes})="([A-Za-z_]\\w*)"`, "g"),
    )) {
      eventHandlerCount += 1;
      if (!new RegExp(`\\b${match[1]}\\s*\\(`).test(code)) {
        contractFailures.push(
          `${path.relative(windowsRoot, file)}: event handler ${match[1]} is not declared in ${path.basename(codeBehind)}`,
        );
      }
    }
  }
  const fields = [];
  for (const match of source.matchAll(
    /<([A-Za-z_][\w.:]*)\b[^>]*\bx:Name="([A-Za-z_]\w*)"[^>]*>/g,
  )) {
    const tag = match[1].split(":").at(-1);
    fields.push({
      name: match[2],
      type: `Microsoft.UI.Xaml.Controls.${tag}`,
    });
  }
  classFields.set(className, fields);
}

if (contractFailures.length > 0) {
  for (const failure of contractFailures) console.error(failure);
  process.exit(1);
}

console.log(
  `WinUI XAML contract verified: ${classFields.size} pages/classes, ` +
    `${eventHandlerCount} event handlers resolve to code-behind methods.`,
);

const generated = [
  "// Generated only for cross-platform C# semantic validation.",
  "#nullable enable",
];
for (const [fullName, fields] of classFields) {
  const split = fullName.lastIndexOf(".");
  const namespaceName = fullName.slice(0, split);
  const className = fullName.slice(split + 1);
  generated.push(
    `namespace ${namespaceName}`,
    "{",
    `public sealed partial class ${className}`,
    "{",
    "    private void InitializeComponent() { }",
    ...fields.map(
      ({ name, type }) => `    private ${type} ${name} = null!;`,
    ),
    "}",
    "}",
  );
}
fs.writeFileSync(
  path.join(temporaryRoot, "XamlFields.g.cs"),
  `${generated.join("\n")}\n`,
);

const result = spawnSync(
  dotnet,
  [
    "build",
    path.join(temporaryRoot, "CodeBehindCheck.csproj"),
    "--configuration",
    "Release",
    "-p:Platform=x64",
  ],
  {
    cwd: windowsRoot,
    encoding: "utf8",
    stdio: "inherit",
  },
);

if (result.error) throw result.error;
const exitCode = result.status ?? 1;
fs.rmSync(temporaryRoot, { recursive: true, force: true });
process.exit(exitCode);
