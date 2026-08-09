# AI Memory
# Copyright © 2026 douxy1994
# SPDX-License-Identifier: AGPL-3.0-only
#
[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [ValidateSet("x64")]
    [string]$Platform = "x64",
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot | Split-Path -Parent
Push-Location $root
try {
    $appProject = Join-Path $root "Windows/src/AIMemory.Windows/AIMemory.Windows.csproj"
    dotnet build $appProject `
        --configuration $Configuration `
        --no-restore `
        -p:Platform=$Platform `
        -p:GenerateAppxPackageOnBuild=true `
        -p:AppxPackageSigningEnabled=false

    $msix = Get-ChildItem `
        (Join-Path $root "Windows/src/AIMemory.Windows/bin/$Platform/$Configuration") `
        -Recurse -Filter "AIMemory.Windows_0.1.3.0_x64.msix" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $msix) {
        throw "Final x64 MSIX payload was not generated."
    }

    $output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        Join-Path $root "release/0.1.3"
    } else {
        [IO.Path]::GetFullPath($OutputDirectory)
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $publishDirectory = Join-Path $root "Windows/installer/bin/$Configuration"
    dotnet publish (Join-Path $root "Windows/installer/AIMemory.Setup.csproj") `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $publishDirectory `
        -p:MsixPath=$($msix.FullName) `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None

    $setup = Join-Path $publishDirectory "AI-Memory-0.1.3-Windows-x64-Setup.exe"
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
        throw "Setup executable was not generated."
    }
    $asset = Join-Path $output "AI-Memory-0.1.3-Windows-x64-Setup.exe"
    Copy-Item -LiteralPath $setup -Destination $asset -Force
    $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $output "AI-Memory-0.1.3-Windows-x64-Setup.exe.sha256") `
        -Value "$hash  AI-Memory-0.1.3-Windows-x64-Setup.exe" `
        -Encoding ascii
    Write-Host "Setup: $asset"
    Write-Host "SHA256: $hash"
} finally {
    Pop-Location
}
