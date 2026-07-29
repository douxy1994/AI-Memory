using Windows.ApplicationModel;

namespace AIMemory.Windows.Services;

public sealed class StartupService
{
    public async Task<StartupTaskState> GetStateAsync() =>
        (await StartupTask.GetAsync("AIMemoryStartup")).State;

    public async Task<StartupTaskState> SetEnabledAsync(bool enabled)
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

    public Task<bool> OpenSystemSettingsAsync() =>
        global::Windows.System.Launcher.LaunchUriAsync(
            new Uri("ms-settings:startupapps")).AsTask();
}
