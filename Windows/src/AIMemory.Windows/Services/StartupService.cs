// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using Windows.ApplicationModel;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace AIMemory.Windows.Services;

public sealed class StartupService
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AI Memory";
    private const string AppUserModelId =
        "com.aimemory.windows_mycvgsb8ewnqm!App";

    public async Task<StartupTaskState> GetStateAsync()
    {
        if (UsesRegistryStartup())
        {
            return RegistryStartupEnabled()
                ? StartupTaskState.Enabled
                : StartupTaskState.Disabled;
        }
        try
        {
            return (await StartupTask.GetAsync("AIMemoryStartup")).State;
        }
        catch (COMException exception) when (
            exception.HResult == unchecked((int)0x80070490))
        {
            return RegistryStartupEnabled()
                ? StartupTaskState.Enabled
                : StartupTaskState.Disabled;
        }
        catch (InvalidOperationException)
        {
            return RegistryStartupEnabled()
                ? StartupTaskState.Enabled
                : StartupTaskState.Disabled;
        }
    }

    public async Task<StartupTaskState> SetEnabledAsync(bool enabled)
    {
        if (UsesRegistryStartup())
        {
            SetRegistryStartup(enabled);
            return enabled
                ? StartupTaskState.Enabled
                : StartupTaskState.Disabled;
        }
        try
        {
            var task = await StartupTask.GetAsync("AIMemoryStartup");
            if (enabled)
            {
                return task.State == StartupTaskState.Enabled
                    ? task.State
                    : await task.RequestEnableAsync();
            }
            task.Disable();
            return task.State;
        }
        catch (COMException exception) when (
            exception.HResult == unchecked((int)0x80070490))
        {
            SetRegistryStartup(enabled);
            return enabled
                ? StartupTaskState.Enabled
                : StartupTaskState.Disabled;
        }
        catch (InvalidOperationException)
        {
            SetRegistryStartup(enabled);
            return enabled
                ? StartupTaskState.Enabled
                : StartupTaskState.Disabled;
        }
    }

    public Task<bool> OpenSystemSettingsAsync() =>
        global::Windows.System.Launcher.LaunchUriAsync(
            new Uri("ms-settings:startupapps")).AsTask();

    private static bool UsesRegistryStartup()
    {
        try
        {
            return Package.Current.IsDevelopmentMode;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool RegistryStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string value
            && !string.IsNullOrWhiteSpace(value);
    }

    private static void SetRegistryStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("无法打开当前用户启动注册表。");
        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }
        key.SetValue(
            RunValueName,
            $"explorer.exe shell:AppsFolder\\{AppUserModelId}",
            RegistryValueKind.String);
    }
}
