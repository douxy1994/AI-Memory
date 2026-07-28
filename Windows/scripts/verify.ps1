$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet restore .\AIMemory.Windows.slnx
    dotnet test .\tests\AIMemory.Core.Tests\AIMemory.Core.Tests.csproj `
        --configuration Release --no-restore
    dotnet build .\src\AIMemory.Windows\AIMemory.Windows.csproj `
        --configuration Release --no-restore -p:Platform=x64
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
