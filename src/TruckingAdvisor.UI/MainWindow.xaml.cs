using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using TruckingAdvisor.AI;
using TruckingAdvisor.Audio;
using TruckingAdvisor.Shared.Input;
using InputBinding = TruckingAdvisor.Shared.Input.InputBinding;
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
    private static string KeybindingsPath => Path.Combine(AppDataDir, "keybindings.json"); // older versions (keys only)
    private static string ControlsPath => Path.Combine(AppDataDir, "controls.json");

    private readonly AtsMemoryMappedTelemetryProvider _telemetry;
    private readonly AiSettings _aiSettings;
    private readonly OpenAiClientService _openAi;
    private readonly TruckingAdvisorDatabase _db;
    private readonly VirtualCompanyProfile _company;
    private readonly WindowsAudioSink _sink = new();
    private readonly AudioPriorityManager _audio;
    private readonly CBRadioService _cbRadio;
    private readonly GlobalKeyboardHook _keyboardHook = new();
    private readonly InputBindingSet _bindings;
    private readonly InputRouter _input;
    private GameControllerInput? _controllers;
    private readonly MicrophoneRecorder _mic = new();
    private readonly WindowsSpeechToText _localStt = new();
    private readonly GitHubPluginInstallerService _pluginInstaller = new();
    private readonly MainDashboardViewModel _viewModel;
    private HudOverlayWindow? _overlay;
    private HudChannelBar? _channelBar;
    private string? _lastAdvisory;
    /// <summary>Set by "--screenshot &lt;folder&gt;": render every tab to PNG, then exit (used by CI and for docs).</summary>
    private readonly string? _screenshotDir;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(AppDataDir);

        var args = Environment.GetCommandLineArgs();
        bool demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        int shot = Array.FindIndex(args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (shot >= 0 && shot + 1 < args.Length) _screenshotDir = args[shot + 1];
        _telemetry = new AtsMemoryMappedTelemetryProvider(enableDemoMode: demo);

        _aiSettings = AiSettings.Load();
        _openAi = new OpenAiClientService(_aiSettings, new AiUsageTracker(AiUsageTracker.DefaultPath));
        _db = new TruckingAdvisorDatabase();
        _company = _db.LoadCompany();
        var career = _db.LoadCareer();
        var roster = _db.MergeWithSaved(PersonalityCatalog.CreateDefaultRoster());

        _audio = new AudioPriorityManager(_sink);
        _audio.AudioStarted += (_, item) => { if (item.Channel == "advisor") _lastAdvisory = item.Text; };
        _cbRadio = new CBRadioService(roster, _company, _audio, () => _viewModel?.EnrichedSnapshot ?? _telemetry.LatestSnapshot, _aiSettings,
            llm: _openAi, stt: new FallbackSpeechToText(_openAi, _localStt), tts: _openAi)
        {
            RealtimeTransportFactory = () => new WebSocketRealtimeTransport(),
            RealtimeApiKey = () => ApiKeyStore.Load(_aiSettings.Provider),
            RealtimeUsage = _openAi.Usage
        };

        _viewModel = new MainDashboardViewModel(
            _telemetry, new DrivingAnalyzer(), new FuelAndMaintenanceAdvisor(), new TripTracker(),
            _audio, _cbRadio, _company, career, _aiSettings,
            postToUi: action => Dispatcher.BeginInvoke(action),
            db: _db);
        DataContext = _viewModel;
        // Keep the newest radio message in view, like a chat app.
        _viewModel.RadioMessages.CollectionChanged += (_, _) =>
        {
            if (_viewModel.RadioMessages.Count > 0) RadioList.ScrollIntoView(_viewModel.RadioMessages[^1]);
        };
        _viewModel.AttachDispatcher(new DispatcherService(_aiSettings, (facts, important, ct) => _cbRadio.SendDispatchMessageAsync(facts, important, ct)));
        _viewModel.MuteRequested += (_, mute) => { _sink.Muted = mute; if (mute) _audio.StopAll(); };
        _viewModel.HandleChangeProposed += (_, handle) =>
        {
            if (MessageBox.Show(this, $"You said your CB handle is \"{handle}\". Save it as your handle?", "CB Handle",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _viewModel.ConfirmHandle(handle);
        };
        ApplyAudioSettings();

        // Every function can be bound to keys and to wheel / button-box buttons (More > Controls).
        // All input goes through one router on the UI thread. Hook callbacks must return fast
        // (Windows drops slow low-level hooks), so they only queue the event.
        _bindings = InputBindingSet.Load(ControlsPath, KeybindingsPath);
        _input = new InputRouter(_bindings);
        _input.ActionPressed += (_, action) =>
        {
            if (action == InputActions.PushToTalk) StartTransmit();
            else HandleAction(action);
        };
        _input.ActionReleased += async (_, action) =>
        {
            if (action == InputActions.PushToTalk) await EndTransmitAsync();
        };
        _keyboardHook.KeyDown += vk => Dispatcher.BeginInvoke(() => _input.Press(new InputBinding(InputSource.Keyboard, vk)));
        _keyboardHook.KeyUp += vk => Dispatcher.BeginInvoke(() => _input.Release(new InputBinding(InputSource.Keyboard, vk)));
        _keyboardHook.Install();
        SourceInitialized += (_, _) => StartControllers();
        UpdatePttLabels();

        RestoreWindowBounds();
        ApplyLayout();
        _telemetry.Start();
        if (_screenshotDir is not null)
        {
            Loaded += async (_, _) => await CaptureScreenshotsAsync(_screenshotDir);
            return;
        }
        _ = Task.Run(RefreshGameStatus);
        Loaded += (_, _) => ShowWelcomeIfFirstRun();
    }

    private void ShowWelcomeIfFirstRun()
    {
        if (_aiSettings.WelcomeShown) return;
        _aiSettings.WelcomeShown = true;
        var dlg = new WelcomeWindow(_aiSettings, _company) { Owner = this };
        dlg.ShowDialog();
        try { _aiSettings.Save(); _db.SaveCompany(_company); } catch { }
        if (dlg.OpenAiSettings) BtnAiSettings_Click(this, new RoutedEventArgs());
    }

    /// <summary>Renders each tab to a PNG (dashboard.png, career.png, ...) and closes the app.</summary>
    private async Task CaptureScreenshotsAsync(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            await Task.Delay(4000); // let telemetry/demo data fill the gauges
            // One typed call so the radio shows chat bubbles (offline replies; never blocks the capture).
            _cbRadio.BeginPushToTalk();
            await Task.WhenAny(_cbRadio.EndPushToTalkAsync("Breaker one-nine, Big Red, you got your ears on?"), Task.Delay(5000));
            var root = (FrameworkElement)Content;
            string[] names = { "dashboard", "career", "contacts", "statistics", "trips" };
            for (int i = 0; i < Tabs.Items.Count && i < names.Length; i++)
            {
                Tabs.SelectedIndex = i;
                await Task.Delay(400);
                SaveElementPng(root, Background, Path.Combine(dir, names[i] + ".png"));
            }
            Tabs.SelectedIndex = 0;
            ToggleOverlay();
            await Task.Delay(800);
            if (_overlay?.Content is FrameworkElement hud)
                SaveElementPng(hud, System.Windows.Media.Brushes.DimGray, Path.Combine(dir, "hud.png"));
            if (_channelBar is not null)
            {
                _channelBar.Flash();
                await Task.Delay(600);
                if (_channelBar.Content is FrameworkElement bar)
                    SaveElementPng(bar, System.Windows.Media.Brushes.DimGray, Path.Combine(dir, "hud-channels.png"));
            }
            _channelBar?.Hide();
            _overlay?.Hide();
            var controls = new ControlsWindow(_bindings, _input, () => new[] { "(example) Wheel" }, () => { }) { Owner = this, ShowActivated = false };
            controls.Show();
            await Task.Delay(600);
            if (controls.Content is FrameworkElement cw) SaveElementPng(cw, controls.Background, Path.Combine(dir, "controls.png"));
            controls.Close();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); } catch { }
        }
        Close();
    }

    private static void SaveElementPng(FrameworkElement element, System.Windows.Media.Brush background, string path)
    {
        element.UpdateLayout();
        var size = new Size(Math.Max(1, element.ActualWidth), Math.Max(1, element.ActualHeight));
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(size));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(element), null, new Rect(size));
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
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
        _localStt.Enabled = _aiSettings.OfflineSpeechRecognition;
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
    private void BtnChannelPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int ch)) _viewModel.SelectChannel(ch);
    }

    // ---- Hotkeys ----

    private void HandleAction(string actionId)
    {
        switch (actionId)
        {
            case "toggleOverlay": ToggleOverlay(); break;
            case "showChannelPicker":
                if (_overlay is not { IsVisible: true }) ToggleOverlay();
                _channelBar?.Flash();
                break;
            case "toggleQuiet": _viewModel.ToggleQuiet(); break;
            case "radioVolumeUp": _viewModel.RunCommand(VoiceCommandKind.VolumeUp); break;
            case "radioVolumeDown": _viewModel.RunCommand(VoiceCommandKind.VolumeDown); break;
            case "distanceReport": _viewModel.RunCommand(VoiceCommandKind.DistanceReport); break;
            case "cycleCbChannel": _viewModel.CycleChannel(1); _channelBar?.Flash(); break;
            case "previousCbChannel": _viewModel.CycleChannel(-1); _channelBar?.Flash(); break;
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

    private void BtnControls_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ControlsWindow(_bindings, _input, () => _controllers?.DeviceNames ?? Array.Empty<string>(), SaveBindings) { Owner = this };
        dlg.ShowDialog();
        _input.CancelCapture();
        UpdatePttLabels();
    }

    private void SaveBindings()
    {
        try { _bindings.Save(ControlsPath); }
        catch (Exception ex) { ShowNotice($"Could not save controls: {ex.Message}"); }
        UpdatePttLabels();
    }

    /// <summary>Wheels, button boxes, joysticks and gamepads (DirectInput, background, non-exclusive).</summary>
    private void StartControllers()
    {
        if (_screenshotDir is not null) return;
        try
        {
            _controllers = new GameControllerInput(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            _controllers.ButtonDown += b => Dispatcher.BeginInvoke(() => _input.Press(b));
            _controllers.ButtonUp += b => Dispatcher.BeginInvoke(() => _input.Release(b));
            _controllers.Error += msg => Dispatcher.BeginInvoke(() => ShowNotice(msg));
            _controllers.Start();
        }
        catch (Exception ex)
        {
            ShowNotice($"Wheel/button-box input is unavailable: {ex.Message}");
        }
    }

    private void BtnQuiet_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleQuiet();

    private void BtnDismissBanner_Click(object sender, RoutedEventArgs e) => _viewModel.DismissBanner();

    /// <summary>Any choice in the "More" menu closes the menu.</summary>
    private void MorePopup_Click(object sender, RoutedEventArgs e) => BtnMore.IsChecked = false;

    /// <summary>The talk button shows what push-to-talk is bound to.</summary>
    private void UpdatePttLabels()
    {
        var ptt = _bindings.For(InputActions.PushToTalk);
        string bound = ptt.Count == 0 ? "NOT BOUND - SEE MORE > CONTROLS" : string.Join(" / ", ptt.Take(2).Select(b => b.Display.ToUpperInvariant()));
        BtnHoldPtt.Content = $"HOLD TO TALK ({bound})";
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

    private async void BtnCallContact_Click(object sender, RoutedEventArgs e)
    {
        if (LstContacts.SelectedItem is not ContactView c) return;
        Tabs.SelectedIndex = 0; // back to the radio
        await _viewModel.CallContactAsync(c.Handle);
    }

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

    // ---- Layout & theme ----

    private void BtnLayout_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new LayoutWindow(_aiSettings) { Owner = this };
        dlg.Applied += (_, _) => ApplyLayout();
        dlg.ShowDialog();
    }

    /// <summary>Applies theme, dashboard widget order/visibility and HUD rows from settings.</summary>
    private void ApplyLayout()
    {
        ThemeManager.Apply(_aiSettings.Theme, _aiSettings.Accent);

        var widgets = new Dictionary<string, FrameworkElement>
        {
            ["Gauges"] = WGauges, ["Delivery"] = WDelivery, ["Advisories"] = WAdvisories, ["Trend"] = WTrend, ["Report"] = WReport
        };
        var order = _aiSettings.WidgetOrder.Where(widgets.ContainsKey)
            .Concat(AiSettings.DefaultWidgetOrder.Where(id => !_aiSettings.WidgetOrder.Contains(id))).ToList();
        DashboardStack.Children.Clear();
        foreach (var id in order)
        {
            var w = widgets[id];
            w.Visibility = _aiSettings.HiddenWidgets.Contains(id) ? Visibility.Collapsed : Visibility.Visible;
            DashboardStack.Children.Add(w);
        }

        var gauges = new Dictionary<string, FrameworkElement> { ["Speed"] = GSpeed, ["Engine"] = GEngine, ["Fuel"] = GFuel, ["Score"] = GScore };
        foreach (var (id, card) in gauges)
            card.Visibility = _aiSettings.HiddenWidgets.Contains("Gauge:" + id) ? Visibility.Collapsed : Visibility.Visible;
        WGauges.Columns = Math.Max(1, gauges.Values.Count(g => g.Visibility == Visibility.Visible));

        _overlay?.SetRows(_aiSettings.HudRows, _aiSettings.HudOpacity);
        SyncChannelBar();
    }

    // ---- PDF & image exports ----

    private void BtnExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (LstTrips.SelectedItem is DeliveryTripRecord t)
            SaveBytes($"delivery-report-{t.StartedAt:yyyyMMdd-HHmm}.pdf", "PDF files|*.pdf", PdfReportWriter.DeliveryReport(t));
        else MessageBox.Show(this, "Select a trip first.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnExportAllPdf_Click(object sender, RoutedEventArgs e) =>
        SaveBytes("trip-history.pdf", "PDF files|*.pdf",
            PdfReportWriter.TripHistory(_viewModel.AllTrips, _company.DisplayHandle, _company.CompanyName));

    /// <summary>Renders a report card for the selected trip to a PNG you can post on Discord etc.</summary>
    private void BtnExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (LstTrips.SelectedItem is not DeliveryTripRecord t)
        {
            MessageBox.Show(this, "Select a trip first.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SaveBytes($"delivery-{t.StartedAt:yyyyMMdd-HHmm}.png", "PNG images|*.png", ReportCard.RenderPng(t, _company.DisplayHandle));
    }

    private void SaveBytes(string fileName, string filter, byte[] content)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = fileName, Filter = filter };
        if (dlg.ShowDialog(this) == true) File.WriteAllBytes(dlg.FileName, content);
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
            SyncChannelBar();
            return;
        }
        if (_overlay is null)
        {
            _overlay = new HudOverlayWindow { DataContext = _viewModel };
            _overlay.ApplySavedBounds(_aiSettings.HudLeft, _aiSettings.HudTop, _aiSettings.HudWidth, _aiSettings.HudHeight);
            _overlay.SetRows(_aiSettings.HudRows, _aiSettings.HudOpacity);
        }
        _overlay.Show();
        SyncChannelBar();
    }

    /// <summary>The channel picker shows under the HUD whenever the HUD is shown (if enabled in Layout).</summary>
    private void SyncChannelBar()
    {
        bool want = _aiSettings.HudChannelPicker && _overlay is { IsVisible: true };
        if (!want)
        {
            _channelBar?.Hide();
            return;
        }
        if (_channelBar is null)
        {
            _channelBar = new HudChannelBar(_viewModel);
            _channelBar.AttachTo(_overlay!);
        }
        _channelBar.Show();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowBounds();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _keyboardHook.Dispose();
        _controllers?.Dispose();
        _mic.Dispose();
        _viewModel.Dispose();
        _telemetry.Dispose();
        _audio.Dispose();
        _channelBar?.Close();
        _overlay?.Close();
        base.OnClosed(e);
    }
}
