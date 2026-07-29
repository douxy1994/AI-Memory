using AIMemory.Core.Models;
using AIMemory.Core.Services;

namespace AIMemory.Windows.Services;

public sealed class AgentIntegrationService
{
    private readonly AgentIntegrationManager _manager = new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Path.Combine(
            AppContext.BaseDirectory,
            "Helpers",
            "aimemory-mcp.exe"));

    public IReadOnlyList<AgentIntegrationStatus> Detect() =>
        _manager.Detect();

    public void SetEnabled(AgentIntegrationStatus status, bool enabled) =>
        _manager.SetEnabled(status, enabled);
}
