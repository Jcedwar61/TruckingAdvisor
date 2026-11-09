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

        _telemetry.Start();
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
        var games = _pluginInstaller.DiscoverGameBinFolders();
        var release = GitHubPluginInstallerService.RenCloudPlugin;
        if (MessageBox.Show(this,
                $"Download {release.OwnerRepo} {release.Tag} from GitHub and install scs-telemetry.dll into:\n\n" +
                (games.Count > 0 ? string.Join("\n", games) : "(no ATS/ETS2 install found - the DLL will only be downloaded)") +
                "\n\nExisting plugin files are backed up first. Close the game before continuing.",
                "Install Telemetry Plugin", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

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
        }
    }

    private void BtnToggleOverlay_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

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
        _overlay ??= new HudOverlayWindow { DataContext = _viewModel };
        _overlay.Show();
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
