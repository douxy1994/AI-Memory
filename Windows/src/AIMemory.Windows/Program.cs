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
                StartupDiagnostics.Write("program.instance.redirecting");
                main.RedirectActivationToAsync(current.GetActivatedEventArgs())
                    .AsTask().GetAwaiter().GetResult();
                StartupDiagnostics.Write("program.instance.redirected");
                return;
            }

            StartupDiagnostics.Reset();
            StartupDiagnostics.Write("program.begin");
            StartupDiagnostics.Write("program.com.initialized");
            StartupDiagnostics.Write("program.instance.current");
            StartupDiagnostics.Write("program.ui.starting");
            Application.Start(_initialization =>
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(queue));
                new App(main);
                StartupDiagnostics.Write("program.app.constructed");
            });
            StartupDiagnostics.Write("program.ui.returned");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("program.failed", exception);
            throw;
        }
    }
}
