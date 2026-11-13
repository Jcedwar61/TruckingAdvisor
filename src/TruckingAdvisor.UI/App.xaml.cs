using System.Threading;
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
        base.OnStartup(e);
    }

    // Not released explicitly: Windows frees it when the process has fully ended, which is exactly
    // when the installer can safely replace the exe.
}
