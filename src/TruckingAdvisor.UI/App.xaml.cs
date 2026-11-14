using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TruckingAdvisor.UI;

public partial class App : Application
{
    /// <summary>
    /// Held while the app runs. The installer's update mode waits for it to disappear before
    /// replacing files, so an update never fights a running copy.
    /// </summary>
    private static Mutex? _running;

    protected override void OnStartup(StartupEventArgs e)
    {
        try { _running = new Mutex(false, @"Local\TruckingAdvisor.Running"); } catch { /* not critical */ }
        var dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckingAdvisor");
        TruckingAdvisor.Shared.Diagnostics.AppLog.Init(System.IO.Path.Combine(dataDir, "logs"));
        TruckingAdvisor.Shared.Diagnostics.AppLog.Info(
            $"TruckingAdvisor {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} starting on {Environment.OSVersion.VersionString}");

        // Anything unexpected is written to the log. UI-thread errors are shown and the app keeps running.
        DispatcherUnhandledException += (_, ex) =>
        {
            TruckingAdvisor.Shared.Diagnostics.AppLog.Error("Unhandled UI error", ex.Exception);
            ex.Handled = true;
            try
            {
                MessageBox.Show($"Something went wrong: {ex.Exception.Message}\n\nTruckingAdvisor keeps running. Details were written to the log " +
                                "(More > Copy Diagnostics puts them on the clipboard for a bug report).",
                    "TruckingAdvisor", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            TruckingAdvisor.Shared.Diagnostics.AppLog.Error("Fatal error", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            TruckingAdvisor.Shared.Diagnostics.AppLog.Error("Background task error", ex.Exception);
            ex.SetObserved();
        };
        base.OnStartup(e);
    }

    // Not released explicitly: Windows frees it when the process has fully ended, which is exactly
    // when the installer can safely replace the exe.
}
