$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet restore .\AIMemory.Windows.slnx
    dotnet test .\tests\AIMemory.Core.Tests\AIMemory.Core.Tests.csproj `
        --configuration Release --no-restore
    dotnet build .\src\AIMemory.Windows\AIMemory.Windows.csproj `
        --configuration Release --no-restore -p:Platform=x64

    $helper = Get-ChildItem `
        .\src\AIMemory.Windows\bin\Release `
        -Recurse -Filter aimemory-mcp.exe |
        Select-Object -First 1
    if (-not $helper) {
        throw "Packaged aimemory-mcp.exe was not found."
    }
    $requests = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'
        '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'
        '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
    )
    $responses = $requests | & $helper.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "aimemory-mcp.exe exited with code $LASTEXITCODE."
    }
    $initialize = $responses[0] | ConvertFrom-Json
    $tools = $responses[1] | ConvertFrom-Json
    if ($initialize.result.serverInfo.name -ne "aimemory") {
        throw "MCP initialize response is invalid."
    }
    if ($initialize.result.protocolVersion -ne "2025-03-26") {
        throw "MCP protocol version is not aligned with the macOS helper."
    }
    $toolNames = @($tools.result.tools | ForEach-Object name)
    foreach ($required in @(
        "get_repo_memory",
        "get_project_context",
        "get_repo_memory_health",
        "import_all_local_history",
        "scan_repo_conversations",
        "merge_repo_alias",
        "search_repo_history",
        "read_history_conversation",
        "create_memory_candidate",
        "propose_memory_merge",
        "list_memory_candidates",
        "create_checkpoint",
        "build_handoff_packet",
        "list_active_runs",
        "list_run_artifacts",
        "resume_from_checkpoint",
        "list_repo_wiki_pages",
        "rebuild_repo_wiki",
        "rebuild_repo_embeddings",
        "list_memory_conflicts",
        "list_entity_graph",
        "detect_agent_integrations"
    )) {
        if ($required -notin $toolNames) {
            throw "MCP tool is missing: $required"
        }
    }

    dotnet build .\src\AIMemory.Windows\AIMemory.Windows.csproj `
        --configuration Release --no-restore -p:Platform=ARM64

    [xml](Get-Content .\src\AIMemory.Windows\Package.appxmanifest) | Out-Null
    $parity = Get-Content .\parity.json | ConvertFrom-Json
    $pending = @($parity.features | Where-Object status -ne "implemented")
    Write-Host "Windows source verification passed."
    Write-Host ("Parity: {0}/{1} implemented." -f `
        ($parity.features.Count - $pending.Count), $parity.features.Count)
    if ($pending.Count -gt 0) {
        Write-Host "Remaining parity work:"
        $pending | ForEach-Object { Write-Host (" - {0}: {1}" -f $_.id, $_.status) }
    }
}
finally {
    Pop-Location
}
