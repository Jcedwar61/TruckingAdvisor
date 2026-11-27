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
    private static bool _showingError;
    private static DateTime _lastErrorBox = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        // The elevated half of "Install SimHub plugin" (SimHub in Program Files): write the plugin and exit with a
        // code. No window, no log, no single-instance mutex (that would make a pending update wait for it).
        if (e.Args.Length > 0 && string.Equals(e.Args[0], SimHubPluginInstaller.ElevatedInstallArg, StringComparison.Ordinal))
        {
            int code = SimHubPluginInstaller.RunElevatedInstall(e.Args[1..]);
            Environment.Exit(code);
            return;
        }

        try { _running = new Mutex(false, @"Local\TruckingAdvisor.Running"); } catch { /* not critical */ }
        var dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckingAdvisor");
        TruckingAdvisor.Shared.Diagnostics.AppLog.Init(System.IO.Path.Combine(dataDir, "logs"));
        TruckingAdvisor.Shared.Diagnostics.AppLog.Info(
            $"TruckingAdvisor {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} starting on {Environment.OSVersion.VersionString}");

        // Anything unexpected is written to the log. UI-thread errors are shown and the app keeps running.
        DispatcherUnhandledException += (_, ex) =>
        {
            // Log the whole chain: WPF's wording is "see the inner exception", and that's where the cause is.
            for (Exception? inner = ex.Exception; inner is not null; inner = inner.InnerException)
                TruckingAdvisor.Shared.Diagnostics.AppLog.Error(inner.InnerException is null ? "Unhandled UI error" : "  caused by", inner);
            ex.Handled = true;
            // At most one message box at a time and one a minute. A box keeps the window running, so a
            // repeating error would otherwise open box after box until the app runs out of stack.
            if (_showingError || DateTime.UtcNow - _lastErrorBox < TimeSpan.FromMinutes(1))
            {
                (MainWindow as MainWindow)?.ShowErrorNotice(ex.Exception.GetBaseException().Message.Replace("\\n", " "));
                return;
            }
            _showingError = true;
            _lastErrorBox = DateTime.UtcNow;
            try
            {
                // WPF writes some of its messages with a literal \n in them; show it as a line break.
                string reason = ex.Exception.GetBaseException().Message.Replace("\\n", Environment.NewLine);
                MessageBox.Show($"Something went wrong: {reason}\n\nTruckingAdvisor keeps running. Details were written to the log " +
                                "(More > Copy Diagnostics puts them on the clipboard for a bug report).",
                    "TruckingAdvisor", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
            finally { _showingError = false; }
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
