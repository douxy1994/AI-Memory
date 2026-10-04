// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using Microsoft.Windows.ApplicationModel.Resources;
using AIMemory.Core.Models;
using AIMemory.Core.Services;

namespace AIMemory.Windows.Services;

public static class LocalizationService
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key)
    {
        try
        {
            var value = Loader.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? key : value;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return key;
        }
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
    public string Detail
    {
        get
        {
            var detail = LocalizationService.Get(Value.State switch
            {
                AgentIntegrationState.Integrated => "AgentStateIntegratedDetail",
                AgentIntegrationState.Partial => "AgentStatePartialDetail",
                AgentIntegrationState.Detected when !Value.IsIntegrationAvailable =>
                    "AgentStateDetectedManualDetail",
                AgentIntegrationState.Detected => "AgentStateDetectedDetail",
                _ => "AgentStateMissingDetail",
            });
            if (Value.DetectionPaths.Count == 0) return detail;
            return $"{detail} · {string.Join(" · ", Value.DetectionPaths)}";
        }
    }
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
    public string DetectionMark => Value.IsDetected ? "●" : "○";
}

public sealed record LocalizedUpgradeReadinessCheck(
    UpgradeReadinessCheck Value)
{
    public string Label => LocalizationService.Get(Value.Key switch
    {
        "settings" => "ReadinessSettingsLabel",
        "webdav_profile" => "ReadinessWebDavProfileLabel",
        "webdav_password" => "ReadinessWebDavPasswordLabel",
        "memory_store" => "ReadinessDatabaseLabel",
        _ => "ReadinessUnknownLabel",
    });

    public string Detail
    {
        get
        {
            var key = Value.DetailCode switch
            {
                "settings_parsed" => "ReadinessSettingsParsed",
                "settings_defaults" => "ReadinessSettingsDefaults",
                "settings_invalid" => "ReadinessSettingsInvalid",
                "webdav_complete" => "ReadinessWebDavComplete",
                "webdav_incomplete" => "ReadinessWebDavIncomplete",
                "webdav_disabled" => "ReadinessWebDavDisabled",
                "password_present" => "ReadinessPasswordPresent",
                "password_missing" => "ReadinessPasswordMissing",
                "password_unavailable" => "ReadinessPasswordUnavailable",
                "password_not_required" => "ReadinessPasswordNotRequired",
                "database_valid" => "ReadinessDatabaseValid",
                "database_invalid" => "ReadinessDatabaseInvalid",
                _ => "ReadinessUnknownDetail",
            };
            return Value.DetailArgument is null
                ? LocalizationService.Get(key)
                : LocalizationService.Format(
                    key,
                    Value.DetailArgument);
        }
    }

    public string StatusGlyph => Value.Status switch
    {
        "ok" => "✓",
        "error" => "×",
        _ => "!",
    };
}
