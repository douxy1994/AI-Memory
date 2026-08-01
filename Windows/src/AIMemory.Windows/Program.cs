using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using AIMemory.Windows.Services;

namespace AIMemory.Windows;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            var current = AppInstance.GetCurrent();
            var main = AppInstance.FindOrRegisterForKey("AIMemory.Main");
            if (!main.IsCurrent)
            {
                main.RedirectActivationToAsync(current.GetActivatedEventArgs())
                    .AsTask().GetAwaiter().GetResult();
                return;
            }

            Application.Start(_initialization =>
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(queue));
                new App(main);
            });
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("program.failed", exception);
            throw;
        }
    }
}
