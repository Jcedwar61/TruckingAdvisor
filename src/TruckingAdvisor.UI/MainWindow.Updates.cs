using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using TruckingAdvisor.Core.Updates;

namespace TruckingAdvisor.UI;

/// <summary>
/// Auto-update: checks GitHub Releases shortly after start and every 12 hours, downloads the new
/// installer in the background, verifies it against the release's SHA256SUMS.txt, then shows a
/// quiet banner. It installs when the player clicks "Restart &amp; update" or (by default) when the
/// app closes, never in the middle of a drive.
/// </summary>
public partial class MainWindow
{
    private static string UpdatesDir => Path.Combine(AppDataDir, "updates");
    private UpdateChecker? _updater;
    private DispatcherTimer? _updateTimer;
    private string? _readyInstaller;
    private UpdateInfo? _readyUpdate;
    private bool _updateLaunched, _checkingUpdates;

    private static Version AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    private void StartUpdateChecks()
    {
        if (_screenshotDir is not null) return;
        _updater = new UpdateChecker(AppVersion);
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(12);
            if (_aiSettings.AutoUpdate) await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
    }

    /// <summary>Manual checks report the result; background checks stay silent unless an update is ready.</summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updater is null || _checkingUpdates) return;
        if (_readyUpdate is not null)
        {
            if (manual) _viewModel.ShowUpdateReady(_readyUpdate.Version.ToString(3), _aiSettings.InstallUpdatesOnExit);
            return;
        }
        _checkingUpdates = true;
        try
        {
            if (manual) ShowNotice("Checking for updates...");
            var update = await _updater.CheckAsync();
            if (update is null)
            {
                if (manual) ShowNotice($"You're up to date (version {_updater.CurrentVersion.ToString(3)}).");
                return;
            }
            if (!manual && string.Equals(_aiSettings.SkippedUpdateVersion, update.Version.ToString(3), StringComparison.Ordinal))
                return;

            if (manual) ShowNotice($"Downloading TruckingAdvisor {update.Version.ToString(3)}...");
            string path = await _updater.DownloadAsync(update, UpdatesDir);
            _readyInstaller = path;
            _readyUpdate = update;
            _viewModel.ShowUpdateReady(update.Version.ToString(3), _aiSettings.InstallUpdatesOnExit);
            if (manual) ShowNotice($"TruckingAdvisor {update.Version.ToString(3)} is downloaded and verified.");
        }
        catch (Exception ex)
        {
            // Offline, GitHub down, or a bad download: try again at the next check. Only manual checks say so.
            if (manual) ShowNotice($"Couldn't check for updates: {ex.Message}");
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    private void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_readyInstaller is null) return;
        if (_cbRadio.CurrentState is not TruckingAdvisor.Shared.Enums.CBRadioState.RadioIdle and not TruckingAdvisor.Shared.Enums.CBRadioState.RadioOff
            && MessageBox.Show(this, "The radio is busy. Restart and update anyway?", "Update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (LaunchInstaller(relaunch: true)) Close();
    }

    private void BtnUpdateLater_Click(object sender, RoutedEventArgs e) => _viewModel.HideUpdate();

    private void BtnUpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_readyUpdate is null) return;
        _aiSettings.SkippedUpdateVersion = _readyUpdate.Version.ToString(3);
        try { _aiSettings.Save(); } catch { }
        _readyInstaller = null;
        _readyUpdate = null;
        _viewModel.HideUpdate();
        ShowNotice("Skipped this version. More > Check for updates installs it if you change your mind.");
    }

    private void BtnUpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_readyUpdate is null || !UpdateChecker.IsTrustedReleasePage(_readyUpdate.ReleasePageUrl)) return;
        try { Process.Start(new ProcessStartInfo(_readyUpdate.ReleasePageUrl) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Starts the verified installer in silent update mode. It waits for this app to exit.</summary>
    private bool LaunchInstaller(bool relaunch)
    {
        if (_readyInstaller is null || _updateLaunched || !File.Exists(_readyInstaller)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(_readyInstaller, relaunch ? "/S /UPDATE /RELAUNCH" : "/S /UPDATE") { UseShellExecute = false });
            _updateLaunched = true;
            return true;
        }
        catch (Exception ex)
        {
            ShowNotice($"Couldn't start the update: {ex.Message}");
            return false;
        }
    }

    /// <summary>Called as the app closes: install a ready update if the player allows it.</summary>
    private void InstallUpdateOnExit()
    {
        _updateTimer?.Stop();
        if (_aiSettings.InstallUpdatesOnExit && !_updateLaunched) LaunchInstaller(relaunch: false);
    }
}
