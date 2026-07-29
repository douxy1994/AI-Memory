using Microsoft.Windows.ApplicationModel.Resources;
using AIMemory.Core.Models;

namespace AIMemory.Windows.Services;

public static class LocalizationService
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key)
    {
        var value = Loader.GetString(key);
        return string.IsNullOrWhiteSpace(value) ? key : value;
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Get(key),
            arguments);
}

public sealed record LocalizedOption(string Id, string Label);

public sealed record LocalizedAgentIntegration(
    AgentIntegrationStatus Value)
{
    public string Label => Value.Label;
    public string Detail => LocalizationService.Get(Value.State switch
    {
        AgentIntegrationState.Integrated => "AgentStateIntegratedDetail",
        AgentIntegrationState.Partial => "AgentStatePartialDetail",
        AgentIntegrationState.Detected when !Value.IsIntegrationAvailable =>
            "AgentStateDetectedManualDetail",
        AgentIntegrationState.Detected => "AgentStateDetectedDetail",
        _ => "AgentStateMissingDetail",
    });
    public string State => LocalizationService.Get(Value.State switch
    {
        AgentIntegrationState.Integrated => "AgentStateIntegrated",
        AgentIntegrationState.Partial => "AgentStatePartial",
        AgentIntegrationState.Detected => "AgentStateDetected",
        _ => "AgentStateMissing",
    });
    public string ActionLabel => LocalizationService.Get(
        Value.IsIntegrated ? "Disable" : "Enable");
    public bool CanToggle => Value.CanToggle;
}
