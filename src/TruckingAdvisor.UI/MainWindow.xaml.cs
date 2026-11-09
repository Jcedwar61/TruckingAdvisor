using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using TruckingAdvisor.AI;
using TruckingAdvisor.Audio;
using TruckingAdvisor.Core.Advisors;
using TruckingAdvisor.Core.Audio;
using TruckingAdvisor.Core.Coaching;
using TruckingAdvisor.Core.Trips;
using TruckingAdvisor.Data;
using TruckingAdvisor.Presentation;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;
using TruckingAdvisor.Telemetry;
using TruckingAdvisor.UI.Overlays;

namespace TruckingAdvisor.UI;

public partial class MainWindow : Window
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckingAdvisor");
    private static string KeybindingsPath => Path.Combine(AppDataDir, "keybindings.json");

    private readonly AtsMemoryMappedTelemetryProvider _telemetry;
    private readonly AiSettings _aiSettings;
    private readonly OpenAiClientService _openAi;
    private readonly TruckingAdvisorDatabase _db;
    private readonly VirtualCompanyProfile _company;
    private readonly WindowsAudioSink _sink = new();
    private readonly AudioPriorityManager _audio;
    private readonly CBRadioService _cbRadio;
    private readonly GlobalPttKeyboardHook _keyboardHook = new();
    private readonly MicrophoneRecorder _mic = new();
    private readonly GitHubPluginInstallerService _pluginInstaller = new();
    private readonly MainDashboardViewModel _viewModel;
    private HudOverlayWindow? _overlay;
    private string? _lastAdvisory;
    private bool _listeningForPttBind;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(AppDataDir);

        bool demo = Environment.GetCommandLineArgs().Contains("--demo", StringComparer.OrdinalIgnoreCase);
        _telemetry = new AtsMemoryMappedTelemetryProvider(enableDemoMode: demo);

        _aiSettings = AiSettings.Load();
        _openAi = new OpenAiClientService(_aiSettings, new AiUsageTracker(AiUsageTracker.DefaultPath));
        _db = new TruckingAdvisorDatabase();
        _company = _db.LoadCompany();
        var career = _db.LoadCareer();
        var roster = _db.MergeWithSaved(PersonalityCatalog.CreateDefaultRoster());

        _audio = new AudioPriorityManager(_sink);
        _audio.AudioStarted += (_, item) => { if (item.Channel == "advisor") _lastAdvisory = item.Text; };
        _cbRadio = new CBRadioService(roster, _company, _audio, () => _telemetry.LatestSnapshot, _aiSettings,
            llm: _openAi, stt: _openAi, tts: _openAi);

        _viewModel = new MainDashboardViewModel(
            _telemetry, new DrivingAnalyzer(), new FuelAndMaintenanceAdvisor(), new TripTracker(),
            _audio, _cbRadio, _company, career, _aiSettings,
            postToUi: action => Dispatcher.BeginInvoke(action),
            db: _db);
        DataContext = _viewModel;
        _viewModel.AttachDispatcher(new DispatcherService(_aiSettings, (facts, important, ct) => _cbRadio.SendDispatchMessageAsync(facts, important, ct)));
        _viewModel.MuteRequested += (_, mute) => { _sink.Muted = mute; if (mute) _audio.StopAll(); };
        _viewModel.HandleChangeProposed += (_, handle) =>
        {
            if (MessageBox.Show(this, $"You said your CB handle is \"{handle}\". Save it as your handle?", "CB Handle",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _viewModel.ConfirmHandle(handle);
        };
        ApplyAudioSettings();

        // Hook callbacks must return fast (Windows drops slow low-level hooks), so never block here.
        _keyboardHook.LoadBindings(KeybindingsPath);
        _keyboardHook.PttPressed += (_, _) => Dispatcher.BeginInvoke(StartTransmit);
        _keyboardHook.PttReleased += (_, _) => Dispatcher.BeginInvoke(new Action(async () => await EndTransmitAsync()));
        _keyboardHook.ActionTriggered += (_, actionId) => Dispatcher.BeginInvoke(() => HandleAction(actionId));
        _keyboardHook.Install();
        UpdatePttLabels();

        RestoreWindowBounds();
        _ = Task.Run(RefreshGameStatus);
        _telemetry.Start();
    }

    // ---- Window size & game detection ----

    private void RestoreWindowBounds()
    {
        if (_aiSettings.WindowWidth is > 0 && _aiSettings.WindowHeight is > 0)
        {
            Width = Math.Max(MinWidth, _aiSettings.WindowWidth.Value);
            Height = Math.Max(MinHeight, _aiSettings.WindowHeight.Value);
        }
        if (_aiSettings.WindowLeft is { } l && _aiSettings.WindowTop is { } t && HudOverlayWindow.IsOnScreen(l, t, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        if (_aiSettings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _aiSettings.WindowLeft = b.Left;
        _aiSettings.WindowTop = b.Top;
        _aiSettings.WindowWidth = b.Width;
        _aiSettings.WindowHeight = b.Height;
        _aiSettings.WindowMaximized = WindowState == WindowState.Maximized;
        if (_overlay is not null)
        {
            _aiSettings.HudLeft = _overlay.Left;
            _aiSettings.HudTop = _overlay.Top;
            _aiSettings.HudWidth = _overlay.Width;
            _aiSettings.HudHeight = _overlay.Height;
        }
        try { _aiSettings.Save(); } catch { /* settings are best effort on exit */ }
    }

    /// <summary>Searches every drive for ATS/ETS2 and reports whether the telemetry plugin is in place.</summary>
    private void RefreshGameStatus()
    {
        IReadOnlyList<GameInstall> games;
        try { games = _pluginInstaller.FindGames(_aiSettings.GameFolders); }
        catch (Exception ex) { games = Array.Empty<GameInstall>(); Dispatcher.BeginInvoke(() => _viewModel.GameStatusText = $"Game search failed: {ex.Message}"); return; }

        string text = games.Count == 0
            ? "American Truck Simulator not found. Use \"Install Telemetry Plugin\" to pick the game folder."
            : string.Join("   ", games.Select(g =>
                $"{g.Name}: {g.InstallFolder} - plugin {(File.Exists(Path.Combine(g.PluginFolder, GitHubPluginInstallerService.RenCloudPlugin.TargetDllName)) ? "installed" : "NOT installed")}"));
        Dispatcher.BeginInvoke(() => _viewModel.GameStatusText = text);

        // First run: the game is there but can't send data yet. Offer to fix it once.
        var missing = games.FirstOrDefault(g => !File.Exists(Path.Combine(g.PluginFolder, GitHubPluginInstallerService.RenCloudPlugin.TargetDllName)));
        if (missing is not null && !_aiSettings.PluginPromptShown)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _aiSettings.PluginPromptShown = true;
                try { _aiSettings.Save(); } catch { }
                if (MessageBox.Show(this,
                        $"Found {missing.Name} at\n{missing.InstallFolder}\n\nbut its telemetry plugin isn't installed yet, so TruckingAdvisor can't read live data.\n\nInstall the plugin now?",
                        "Telemetry Plugin", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    BtnPlugin_Click(this, new RoutedEventArgs());
            });
        }
    }

    private void ApplyAudioSettings()
    {
        _sink.CleanAudio = _aiSettings.CleanAudio;
        _sink.DeviceSelector = channel => channel switch
        {
            "cb" => _aiSettings.RadioOutputDevice,
            "dispatcher" => _aiSettings.DispatcherOutputDevice,
            _ => _aiSettings.AdvisorOutputDevice
        };
        _mic.DeviceNumber = Math.Max(0, _aiSettings.MicrophoneDevice);
        _audio.SetChannelVolume("cb", _aiSettings.RadioVolume);
        _audio.SetChannelVolume("dispatcher", _aiSettings.DispatcherVolume);
        _audio.SetChannelVolume("advisor", _aiSettings.AdvisorVolume);
    }

    // ---- Push-to-talk ----

    private void StartTransmit()
    {
        _cbRadio.BeginPushToTalk();
        if (_cbRadio.CurrentState == CBRadioState.PlayerTransmitting && _aiSettings.MicrophoneEnabled)
        {
            try { _mic.Start(); }
            catch (Exception ex) { ShowNotice($"Microphone error: {ex.Message}"); }
        }
    }

    private async Task EndTransmitAsync()
    {
        if (_mic.IsRecording)
        {
            byte[] wav = await _mic.StopAsync();
            await _cbRadio.EndPushToTalkWithAudioAsync(wav);
        }
        else
        {
            await _cbRadio.EndPushToTalkAsync(null);
            if (!_aiSettings.MicrophoneEnabled)
                ShowNotice("Microphone is off. Type a message and press Enter, or turn the mic on in AI & Radio Settings.");
        }
    }

    private async Task SendTypedAsync()
    {
        string text = TxtMessage.Text.Trim();
        if (text.Length == 0) return;
        TxtMessage.Clear();
        _cbRadio.BeginPushToTalk();
        await _cbRadio.EndPushToTalkAsync(text);
    }

    private void ShowNotice(string message) => _viewModel.ShowNotice(message);

    private void BtnHoldPtt_Down(object sender, MouseButtonEventArgs e) => StartTransmit();
    private async void BtnHoldPtt_Up(object sender, MouseButtonEventArgs e) => await EndTransmitAsync();
    private async void BtnSend_Click(object sender, RoutedEventArgs e) => await SendTypedAsync();
    private async void TxtMessage_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SendTypedAsync();
    }
    private void BtnChannel_Click(object sender, RoutedEventArgs e) => _viewModel.CycleChannel();

    // ---- Hotkeys ----

    private void HandleAction(string actionId)
    {
        switch (actionId)
        {
            case "toggleOverlay": ToggleOverlay(); break;
            case "cycleCbChannel": _viewModel.CycleChannel(); break;
            case "acknowledgeAlert": _audio.StopAll(); break;
            case "emergencyMute": _sink.Muted = !_sink.Muted; if (_sink.Muted) _audio.StopAll(); break;
            case "repeatAdvisory":
                if (_lastAdvisory is { } last) _audio.EnqueueAnnouncement(last, AudioPriority.Normal);
                break;
            case "quickFuelReport":
                _audio.EnqueueAnnouncement(FuelReport(_telemetry.LatestSnapshot), AudioPriority.Normal);
                break;
            case "quickDispatchCheck":
                _cbRadio.SwitchChannel(CBRadioService.DispatchChannel);
                _cbRadio.BeginPushToTalk();
                _ = _cbRadio.EndPushToTalkAsync("Dispatch, checking in. How am I looking?");
                break;
        }
    }

    private static string FuelReport(AtsTelemetrySnapshot s)
    {
        if (!s.HasFuelCapacity) return "Fuel data isn't available right now.";
        string range = s.EstimatedRangeMiles > 0 ? $" About {s.EstimatedRangeMiles:F0} miles of range." : string.Empty;
        string mpg = s.HasFuelEconomy ? $" Averaging {s.AverageMpg:F1} miles per gallon." : string.Empty;
        return $"Fuel at {s.FuelPercent:F0} percent, {s.FuelGallons:F0} gallons.{range}{mpg}";
    }

    // ---- Buttons ----

    private void BtnRebindPtt_Click(object sender, RoutedEventArgs e)
    {
        _listeningForPttBind = true;
        BtnRebindPtt.Content = "Press a key (Esc cancels)...";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_listeningForPttBind) return;
        e.Handled = true;
        _listeningForPttBind = false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Escape)
        {
            _keyboardHook.BindKey("pushToTalk", KeyInterop.VirtualKeyFromKey(key));
            try { _keyboardHook.SaveBindings(KeybindingsPath); } catch { /* non-fatal */ }
        }
        UpdatePttLabels();
    }

    private void UpdatePttLabels()
    {
        var key = KeyInterop.KeyFromVirtualKey(_keyboardHook.PttVirtualKeyCode);
        BtnHoldPtt.Content = $"HOLD TO TALK ({key.ToString().ToUpperInvariant()})";
        BtnRebindPtt.Content = $"PTT Key: {key}";
    }

    private void BtnAiSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AiSettingsWindow(_aiSettings, _openAi, _company) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _db.SaveCompany(_company);
            ApplyAudioSettings();
        }
    }

    private void BtnDemo_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Restart TruckingAdvisor with generated demo data?\n\nDemo values are clearly labeled and never saved as trips. Live telemetry is still used if ATS is running.",
            "Demo Data", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK || Environment.ProcessPath is not { } exe) return;
        Process.Start(new ProcessStartInfo(exe, "--demo") { UseShellExecute = false });
        Close();
    }

    private async void BtnPlugin_Click(object sender, RoutedEventArgs e)
    {
        var release = GitHubPluginInstallerService.RenCloudPlugin;
        BtnPlugin.IsEnabled = false;
        BtnPlugin.Content = "Searching drives...";
        var games = (await Task.Run(() => _pluginInstaller.FindGames(_aiSettings.GameFolders))).ToList();
        BtnPlugin.IsEnabled = true;
        BtnPlugin.Content = "Install Telemetry Plugin";

        // Found nothing, or the player wants a different copy: let them pick the folder.
        string found = games.Count > 0
            ? string.Join("\n", games.Select(g => $"{g.Name}\n   {g.InstallFolder}   (found via {g.Source})"))
            : "No ATS or ETS2 install was found on any drive.";
        var choice = MessageBox.Show(this,
            $"{found}\n\nYes = install {release.OwnerRepo} {release.Tag} into {(games.Count > 0 ? "these games" : "a folder you choose")}\n" +
            "No = choose another game folder first\nCancel = do nothing\n\nExisting plugin files are backed up. Close the game first.",
            "Install Telemetry Plugin", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        if (choice == MessageBoxResult.No || games.Count == 0)
        {
            if (PickGameFolder() is not { } picked) return;
            if (!games.Any(g => string.Equals(g.InstallFolder, picked.InstallFolder, StringComparison.OrdinalIgnoreCase)))
                games.Add(picked);
        }

        BtnPlugin.IsEnabled = false;
        BtnPlugin.Content = "Downloading...";
        try
        {
            var downloaded = await _pluginInstaller.DownloadAsync(Path.Combine(AppDataDir, "Plugins"));
            if (!downloaded.HashVerified &&
                MessageBox.Show(this,
                    $"Downloaded {downloaded.AssetName}.\nSHA-256: {downloaded.AssetSha256}\n\n" +
                    $"This version has no published checksum to compare against. You can check the file on {release.ReleasePageUrl}.\n\nInstall it?",
                    "Confirm Plugin", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var log = _pluginInstaller.InstallToGames(downloaded.StagedDllPath, games);
            MessageBox.Show(this, string.Join("\n", log), "Telemetry Plugin", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Plugin install failed: {ex.Message}\n\nYou can download it manually from {release.ReleasePageUrl} and copy the x64 scs-telemetry.dll into <game>\\bin\\win_x64\\plugins.",
                "Telemetry Plugin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnPlugin.IsEnabled = true;
            BtnPlugin.Content = "Install Telemetry Plugin";
            _ = Task.Run(RefreshGameStatus);
        }
    }

    /// <summary>Lets the player point at the game folder on any drive; remembered for next time.</summary>
    private GameInstall? PickGameFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose your American Truck Simulator (or ETS2) folder"
        };
        if (dlg.ShowDialog(this) != true) return null;
        var game = _pluginInstaller.VerifyGameFolder(dlg.FolderName);
        if (game is null)
        {
            MessageBox.Show(this,
                $"{dlg.FolderName}\n\ndoesn't look like ATS or ETS2 (no bin\\win_x64\\amtrucks.exe or eurotrucks2.exe inside).\n" +
                "In Steam: right-click the game > Manage > Browse local files to find the right folder.",
                "Not a Game Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (!_aiSettings.GameFolders.Contains(game.InstallFolder, StringComparer.OrdinalIgnoreCase))
        {
            _aiSettings.GameFolders.Add(game.InstallFolder);
            try { _aiSettings.Save(); } catch { }
        }
        return game;
    }

    private void BtnToggleOverlay_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

    private void BtnHudLock_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay is not { IsVisible: true }) ToggleOverlay();
        bool unlock = _overlay!.IsLocked;
        _overlay.SetLocked(!unlock);
        BtnHudLock.Content = unlock ? "Lock HUD (done)" : "Move/Resize HUD";
        if (!unlock) SaveWindowBounds();
    }

    // ---- Contacts & trips ----

    private void BtnForgetContact_Click(object sender, RoutedEventArgs e)
    {
        if (LstContacts.SelectedItem is not ContactView c) return;
        if (MessageBox.Show(this, $"Delete everything {c.Handle} remembers about your conversations?", "Forget Conversations",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _viewModel.DeleteContactMemories(c.Id);
    }

    private void BtnExportCsv_Click(object sender, RoutedEventArgs e) =>
        SaveExport("trips.csv", "CSV files|*.csv", TruckingAdvisorDatabase.ExportDeliveryReportCsv(_viewModel.TripHistory));

    private void BtnExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (LstTrips.SelectedItem is DeliveryTripRecord t)
            SaveExport($"trip-{t.StartedAt:yyyyMMdd-HHmm}.json", "JSON files|*.json", TruckingAdvisorDatabase.ExportDeliveryReportJson(t));
    }

    private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (LstTrips.SelectedItem is DeliveryTripRecord t)
            Clipboard.SetText(TruckingAdvisorDatabase.ExportDeliveryReportText(t));
    }

    private void SaveExport(string fileName, string filter, string content)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = fileName, Filter = filter };
        if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, content);
    }

    private void ToggleOverlay()
    {
        if (_overlay is { IsVisible: true })
        {
            _overlay.Hide();
            return;
        }
        if (_overlay is null)
        {
            _overlay = new HudOverlayWindow { DataContext = _viewModel };
            _overlay.ApplySavedBounds(_aiSettings.HudLeft, _aiSettings.HudTop, _aiSettings.HudWidth, _aiSettings.HudHeight);
        }
        _overlay.Show();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowBounds();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _keyboardHook.Dispose();
        _mic.Dispose();
        _viewModel.Dispose();
        _telemetry.Dispose();
        _audio.Dispose();
        _overlay?.Close();
        base.OnClosed(e);
    }
}
