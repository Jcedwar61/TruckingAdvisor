using TruckingAdvisor.Shared;
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
using TruckingAdvisor.Core.Speech;
using TruckingAdvisor.Data;
using TruckingAdvisor.Presentation;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
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
    private readonly LocalWhisperSpeechToText _whisper = new(Path.Combine(AppDataDir, "models"));
    private readonly SpeechRouter _speech;
    private DateTime _lastDownloadNotice = DateTime.MinValue;
    private readonly GitHubPluginInstallerService _pluginInstaller = new();
    private readonly MainDashboardViewModel _viewModel;
    private HudOverlayWindow? _overlay;
    private HudChannelBar? _channelBar;
    private string? _lastAdvisory;
    /// <summary>Set by "--screenshot &lt;folder&gt;": render every tab to PNG, then exit (used by CI and for docs).</summary>
    private readonly string? _screenshotDir;
    /// <summary>Set by "--voice-selftest &lt;folder&gt;": synthesize test lines with each voice engine to WAV, then exit (CI).</summary>
    private readonly string? _voiceSelfTestDir;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(AppDataDir);

        var args = Environment.GetCommandLineArgs();
        bool demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        int shot = Array.FindIndex(args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (shot >= 0 && shot + 1 < args.Length) _screenshotDir = args[shot + 1];
        int voiceTest = Array.FindIndex(args, a => a.Equals("--voice-selftest", StringComparison.OrdinalIgnoreCase));
        if (voiceTest >= 0 && voiceTest + 1 < args.Length) _voiceSelfTestDir = args[voiceTest + 1];
        int replay = Array.FindIndex(args, a => a.Equals("--replay", StringComparison.OrdinalIgnoreCase));
        string? replayFile = replay >= 0 && replay + 1 < args.Length ? args[replay + 1] : null;
        _telemetry = new AtsMemoryMappedTelemetryProvider(enableDemoMode: demo, replayFile: replayFile);

        _aiSettings = AiSettings.Load();
        _openAi = new OpenAiClientService(_aiSettings, new AiUsageTracker(AiUsageTracker.DefaultPath));
        _db = new TruckingAdvisorDatabase();
        _company = _db.LoadCompany();
        var career = _db.LoadCareer();
        var roster = _db.MergeWithSaved(PersonalityCatalog.CreateDefaultRoster());

        _audio = new AudioPriorityManager(_sink);
        // Speech recognition: cleaned clips, recognizers in the order the player chose, all told the names
        // they're likely to hear (handles, dispatcher, cities, CB slang).
        _speech = new SpeechRouter(SpeechOrder);
        _whisper.PromptProvider = SpeechPrompt;
        _openAi.SpeechPromptProvider = SpeechPrompt;
        _whisper.StatusChanged += msg => Dispatcher.BeginInvoke(() => ShowNotice(msg));
        _whisper.DownloadProgress += p => Dispatcher.BeginInvoke(() =>
        {
            if (DateTime.UtcNow - _lastDownloadNotice < TimeSpan.FromSeconds(2) && p < 1) return;
            _lastDownloadNotice = DateTime.UtcNow;
            ShowNotice($"Downloading the offline speech model: {p:P0} (one time only). Until it's ready, Windows recognition is used.");
        });
        _audio.AudioStarted += (_, item) => { if (item.Channel == "advisor") _lastAdvisory = item.Text; };
        _cbRadio = new CBRadioService(roster, _company, _audio, () => _viewModel?.EnrichedSnapshot ?? _telemetry.LatestSnapshot, _aiSettings,
            llm: _openAi, stt: _speech, tts: _openAi)
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
        // Contacts are refreshed in place when a trucker answers; keep the one you're looking at selected.
        LstContacts.SelectionChanged += (_, _) => { if (LstContacts.SelectedItem is ContactView c) _selectedContactId = c.Id; };
        _viewModel.Contacts.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (LstContacts.SelectedItem is null && _selectedContactId is not null)
                LstContacts.SelectedItem = _viewModel.Contacts.FirstOrDefault(x => x.Id == _selectedContactId);
        });
        _viewModel.IsReplay = replayFile is not null;
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
        _sink.VoiceProblem += msg => Dispatcher.BeginInvoke(() => ShowNotice(msg));
        _audio.PlaybackFailed += (_, ex) => Dispatcher.BeginInvoke(() =>
        {
            if (DateTime.UtcNow - _lastPlaybackNotice < TimeSpan.FromMinutes(2)) return;
            _lastPlaybackNotice = DateTime.UtcNow;
            ShowNotice($"A voice line couldn't be played ({ex.Message}). Settings > Test voices checks your speakers.");
        });
        // Pick the working OpenAI models from the built-in lists (no model names for the player to choose).
        _openAi.ApplyResolvedModels();
        _ = _openAi.RefreshModelsAsync();

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
        // F1 opens the guide while this window is active (it isn't a global key, so the game keeps F1).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F1) { GuideWindow.ShowGuide(this, _bindings); e.Handled = true; }
        };
        UpdatePttLabels();

        RestoreWindowBounds();
        ApplyLayout();
        _telemetry.Start();
        if (_screenshotDir is not null)
        {
            Loaded += async (_, _) => await CaptureScreenshotsAsync(_screenshotDir);
            return;
        }
        if (_voiceSelfTestDir is not null)
        {
            Loaded += async (_, _) => await VoiceSelfTestAsync(_voiceSelfTestDir);
            return;
        }
        _ = Task.Run(RefreshGameStatus);
        Loaded += (_, _) => ShowWelcomeIfFirstRun();
        StartUpdateChecks();
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
            string[] names = { "dashboard", "career", "contacts", "statistics", "trips", "map" };
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
            GuideWindow.ShowGuide(this, _bindings, "CB radio");
            await Task.Delay(600);
            var guide = OwnedWindows.OfType<GuideWindow>().FirstOrDefault();
            if (guide?.Content is FrameworkElement gw) SaveElementPng(gw, guide.Background, Path.Combine(dir, "guide.png"));
            guide?.Close();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); } catch { }
        }
        Close();
    }

    /// <summary>Renders a line with the free neural voice and with a Windows voice to WAV files (no speaker needed).</summary>
    private async Task VoiceSelfTestAsync(string dir)
    {
        var report = new System.Text.StringBuilder();
        (float[] Samples, int Rate)? neural = null;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var engine in new[] { VoiceEngine.FreeNeural, VoiceEngine.Windows })
            {
                _sink.Engine = engine;
                var item = new AudioItem("Breaker one-nine, this is Big Red. Ten-four, I copy you loud and clear.", AudioPriority.Normal, "cb",
                    VoiceId: "onyx", NeuralVoice: "en-US-ChristopherNeural|-8%|-8Hz");
                try
                {
                    var (samples, rate, source) = await _sink.RenderAsync(item, CancellationToken.None);
                    string file = Path.Combine(dir, $"voice-{engine}.wav");
                    using (var w = new NAudio.Wave.WaveFileWriter(file, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)))
                        w.WriteSamples(samples, 0, samples.Length);
                    report.AppendLine($"{engine}: {source}, {samples.Length / (double)rate:F1} s at {rate} Hz");
                    if (engine == VoiceEngine.FreeNeural && source.Contains("neural")) neural = (samples, rate);
                }
                catch (Exception ex) { report.AppendLine($"{engine}: FAILED {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { report.AppendLine($"FAILED {ex}"); }

        // Recognition, end to end: the neural voice's line, cleaned like a mic clip, through local Whisper.
        foreach (bool accurate in new[] { true, false })
        {
            try
            {
                _whisper.Accurate = accurate;
                await _whisper.EnsureModelAsync();
                string modelPath = Path.Combine(AppDataDir, "models", _whisper.Model.FileName);
                report.AppendLine($"Model {_whisper.Model.FileName}: sha256 {(File.Exists(modelPath) ? await ModelDownloader.HashAsync(modelPath) : "missing")}");
                if (neural is { } n)
                {
                    var (clean, info) = SpeechAudio.Prepare(n.Samples, n.Rate);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    string? heard = await _whisper.TranscribeSamplesAsync(clean, CancellationToken.None);
                    report.AppendLine($"Whisper {_whisper.Model.Id}: \"{heard}\" in {sw.Elapsed.TotalSeconds:F1} s (gain {info.Gain:F1})");
                }
            }
            catch (Exception ex) { report.AppendLine($"Whisper {( accurate ? "small.en" : "base.en")}: FAILED {ex.GetType().Name}: {ex.Message}"); }
        }
        try { File.WriteAllText(Path.Combine(dir, "voice-selftest.txt"), report.ToString()); } catch { }
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
            ? "ATS / ETS2 not found. Use More > Install Telemetry Plugin to pick the game folder."
            : string.Join("   ", games.Select(g =>
                $"{g.Name}: {g.InstallFolder} - plugin {(File.Exists(Path.Combine(g.PluginFolder, GitHubPluginInstallerService.RenCloudPlugin.TargetDllName)) ? "installed" : "NOT installed")}"));
        bool pluginOk = games.Any(g => File.Exists(Path.Combine(g.PluginFolder, GitHubPluginInstallerService.RenCloudPlugin.TargetDllName)));
        Dispatcher.BeginInvoke(() =>
        {
            _viewModel.GameStatusText = text;
            _viewModel.SetSetupStatus(gameFound: games.Count > 0, pluginInstalled: pluginOk);
        });

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
        _sink.Engine = _aiSettings.VoiceEngine;
        _sink.DefaultNeuralVoice = _aiSettings.CoDriverNeuralVoice;
        _sink.DeviceSelector = channel => channel switch
        {
            "cb" => _aiSettings.RadioOutputDevice,
            "dispatcher" => _aiSettings.DispatcherOutputDevice,
            _ => _aiSettings.AdvisorOutputDevice
        };
        _mic.DeviceNumber = Math.Max(0, _aiSettings.MicrophoneDevice);
        _localStt.Enabled = true; // last resort only
        _whisper.Accurate = _aiSettings.WhisperAccurate;
        _whisper.Enabled = (SpeechRecognition)_aiSettings.SpeechRecognitionMode != SpeechRecognition.WindowsBasic;
        _mic.KeepReady = _aiSettings.KeepMicReady;
        if (_aiSettings.MicrophoneEnabled && _screenshotDir is null && _voiceSelfTestDir is null)
        {
            try { _mic.EnsureReady(); } catch (Exception ex) { ShowNotice($"Microphone error: {ex.Message}"); }
            if (_whisper.Enabled) _ = _whisper.EnsureModelAsync();
        }
        else _mic.Close();
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
            if (_speech.LastWasSilent)
                ShowNotice(_speech.LastClip is { Peak: < 0.02f }
                    ? "Didn't hear anything: your microphone is very quiet or muted. Check the mic in Settings > Test microphone."
                    : "Didn't catch any words. Hold push-to-talk a moment before you start talking, then speak normally.");
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
        string range = s.EstimatedRangeMiles > 0 ? $" About {Units.Dist(s.EstimatedRangeMiles):F0} {Units.DistWord} of range." : string.Empty;
        string mpg = s.HasFuelEconomy ? $" Averaging {Units.Economy(s.AverageMpg):F1} {Units.EconomySpoken}." : string.Empty;
        return $"Fuel at {s.FuelPercent:F0} percent, {Units.Vol(s.FuelGallons):F0} {Units.VolWord}.{range}{mpg}";
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

    private void BtnMapZoomIn_Click(object sender, RoutedEventArgs e) => BigMap.ZoomBy(1.5);
    private void BtnMapZoomOut_Click(object sender, RoutedEventArgs e) => BigMap.ZoomBy(1 / 1.5);
    private void BtnMapFollow_Click(object sender, RoutedEventArgs e) => BigMap.FollowTruck();
    private void BtnMapAll_Click(object sender, RoutedEventArgs e) => BigMap.ShowAll();

    // ---- Diagnostics ----

    private static string RecordingsDir => Path.Combine(AppDataDir, "recordings");

    /// <summary>Records raw telemetry to a file that can be sent with a bug report and replayed exactly.</summary>
    private void BtnRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_telemetry.IsRecording)
        {
            var path = _telemetry.StopRecording();
            BtnRecord.Content = "Record a Session";
            if (path is not null)
            {
                ShowNotice($"Recording saved: {Path.GetFileName(path)}");
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
            }
            return;
        }
        if (MessageBox.Show(this,
                "Record what the game's telemetry plugin reports (speed, truck, cargo, cities, positions...) to a file, to attach to a bug report.\n\n" +
                "It holds no personal data and no audio. Stop it from the same menu; it also stops on its own at 200 MB.",
                "Record a session", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        string file = Path.Combine(RecordingsDir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.tarec");
        try
        {
            _telemetry.StartRecording(file);
            BtnRecord.Content = "Stop Recording";
            ShowNotice("Recording the session. More > Stop Recording when done.");
        }
        catch (Exception ex) { ShowNotice($"Couldn't start recording: {ex.Message}"); }
    }

    /// <summary>Version, Windows, connection state, settings that matter and the recent log, with personal details removed.</summary>
    private void BtnDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var s = _telemetry.LatestSnapshot;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"TruckingAdvisor {AppVersion.ToString(3)} on {Environment.OSVersion.VersionString} ({(Environment.Is64BitProcess ? "64" : "32")}-bit)");
        sb.AppendLine($"Telemetry: {_telemetry.CurrentState} - {_telemetry.StatusDetail}");
        sb.AppendLine($"Game: {(s.GameId == 1 ? "ETS2" : s.GameId == 2 ? "ATS" : "unknown")}, plugin revision {s.PluginRevision}, demo {s.IsDemoData}");
        sb.AppendLine($"AI: {(_aiSettings.GlobalAiEnabled ? "on" : "off")}, voice {_aiSettings.VoicePipeline}, mic {(_aiSettings.MicrophoneEnabled ? "on" : "off")}, units {_aiSettings.Units}");
        sb.AppendLine($"Controllers: {string.Join(", ", _controllers?.DeviceNames ?? Array.Empty<string>())}");
        sb.AppendLine();
        sb.AppendLine("Recent log:");
        foreach (var line in TruckingAdvisor.Shared.Diagnostics.AppLog.Tail(80)) sb.AppendLine(line);
        try
        {
            Clipboard.SetText(TruckingAdvisor.Shared.Diagnostics.AppLog.Scrub(sb.ToString()));
            ShowNotice("Diagnostics copied to the clipboard (no keys, user name or personal paths). Paste them into a bug report.");
        }
        catch (Exception ex) { ShowNotice($"Couldn't copy diagnostics: {ex.Message}"); }
    }

    private void BtnGuide_Click(object sender, RoutedEventArgs e) => GuideWindow.ShowGuide(this, _bindings);

    /// <summary>Errors after the first are shown here instead of as more message boxes.</summary>
    public void ShowErrorNotice(string message) => ShowNotice($"Something went wrong ({message}). Details are in the log: More > Copy Diagnostics.");

    private string? _selectedContactId;

    /// <summary>Recognizers in the order the player chose; unavailable ones are skipped.</summary>
    private IReadOnlyList<(string Name, ISpeechToText Engine)> SpeechOrder()
    {
        ISpeechToText openAi = _openAi;
        var whisper = ("Whisper (this PC)", (ISpeechToText)_whisper);
        var windows = ("Windows (basic)", (ISpeechToText)_localStt);
        return (SpeechRecognition)_aiSettings.SpeechRecognitionMode switch
        {
            SpeechRecognition.OnThisPc => new[] { whisper, windows },
            SpeechRecognition.WindowsBasic => new[] { windows },
            _ => new[] { ("OpenAI", openAi), whisper, windows } // Auto and OpenAI: OpenAI when AI is on
        };
    }

    /// <summary>Names the player is likely to say, so recognizers spell them right.</summary>
    private string SpeechPrompt()
    {
        var s = _telemetry.LatestSnapshot;
        var names = new List<string> { _company.DisplayHandle, _company.DispatcherName, _company.CompanyName, s.OriginCity, s.DestinationCity };
        names.AddRange(_cbRadio.Roster.Select(t => t.Handle));
        return SpeechText.BuildPrompt(names.Where(n => n != "driver"));
    }

    /// <summary>Settings > Test microphone: records a few seconds and shows what was heard.</summary>
    private async Task<string> TestMicrophoneAsync(int seconds)
    {
        if (!_aiSettings.MicrophoneEnabled) return "Turn on \"Use my microphone\" first (and press Save if you just changed it).";
        if (_mic.IsRecording) return "The radio is using the microphone right now. Try again in a moment.";
        float peak = 0;
        _mic.Start();
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { peak = Math.Max(peak, _mic.Level); await Task.Delay(50); }
        var wav = await _mic.StopAsync();
        string level = peak < 0.02f ? "very quiet or muted (check the mic and Windows input volume)"
            : peak < 0.1f ? "quiet (it will be boosted)" : peak > 0.98f ? "too loud (clipping): lower the input volume" : "good";
        string? text = await _speech.TranscribeAsync(wav, CancellationToken.None);
        string heard = text is null
            ? (_speech.LastWasSilent ? "No speech detected." : "Nothing recognized.")
            : $"Heard: \"{text}\" (by {_speech.LastUsed})";
        string model = _whisper.ModelReady ? "" : _whisper.Enabled ? "\nThe offline Whisper model is still downloading; Windows recognition was used meanwhile." : "";
        return $"Mic level: {level}.\n{heard}{model}";
    }
    private DateTime _lastPlaybackNotice = DateTime.MinValue;

    /// <summary>Settings > Test voices: a trucker, dispatch and the co-driver, each through the real audio path.</summary>
    private async Task<string> TestVoicesAsync(VoiceEngine engine)
    {
        var before = _sink.Engine;
        _sink.Engine = engine;
        var lines = new (string Who, AudioItem Item, float Volume)[]
        {
            ("Big Red (CB)", new AudioItem("Breaker one-nine, this is Big Red. I hear you loud and clear, driver.", AudioPriority.Normal, "cb", ApplyCbEffects: true,
                VoiceId: "onyx", NeuralVoice: "en-US-ChristopherNeural|-8%|-8Hz"), _aiSettings.RadioVolume),
            ("Dispatch", new AudioItem($"{_company.DisplayHandle}, this is dispatch. Radio check, you're coming through fine.", AudioPriority.Normal, "dispatcher", ApplyCbEffects: true,
                VoiceId: "coral", NeuralVoice: "en-US-AriaNeural|+4%|+0Hz"), _aiSettings.DispatcherVolume),
            ("Co-driver", new AudioItem("Co-driver here. Voices are working.", AudioPriority.Normal, "advisor"), _aiSettings.AdvisorVolume)
        };
        var results = new List<string>();
        try
        {
            foreach (var (who, item, volume) in lines)
            {
                try
                {
                    var wav = engine == VoiceEngine.OpenAI ? await _openAi.SynthesizeAsync(item.Text, item.VoiceId ?? "onyx", CancellationToken.None) : null;
                    await _sink.PlayAsync(item with { WavAudio = wav }, Math.Max(0.3f, volume), CancellationToken.None);
                    results.Add($"✓ {who}: {_sink.LastVoiceSource}");
                }
                catch (Exception ex)
                {
                    results.Add($"✗ {who}: {ex.Message}");
                }
            }
        }
        finally { _sink.Engine = before; }
        if (engine == VoiceEngine.OpenAI && results.Any(r => r.Contains("OpenAI voice")) == false)
            results.Add("(OpenAI voices need AI on and a saved key; the free voice spoke instead.)");
        return string.Join("\n", results) + "\n\nPress Save to keep this voice choice.";
    }

    private void BtnDismissSetup_Click(object sender, RoutedEventArgs e) => _viewModel.DismissSetupChecklist();

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
        var dlg = new AiSettingsWindow(_aiSettings, _openAi, _company, TestVoicesAsync, TestMicrophoneAsync) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _db.SaveCompany(_company);
            ApplyAudioSettings();
            _viewModel.RefreshUnits();
            _openAi.ApplyResolvedModels();
            _ = _openAi.RefreshModelsAsync();
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
        try { await _viewModel.CallContactAsync(c.Handle); }
        catch (Exception ex)
        {
            TruckingAdvisor.Shared.Diagnostics.AppLog.Error($"Calling {c.Handle} failed", ex);
            ShowNotice($"Couldn't call {c.Handle}: {ex.Message}");
        }
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
        InstallUpdateOnExit();
        _keyboardHook.Dispose();
        _controllers?.Dispose();
        _mic.Dispose();
        _whisper.Dispose();
        _viewModel.Dispose();
        _telemetry.Dispose();
        _audio.Dispose();
        _channelBar?.Close();
        _overlay?.Close();
        base.OnClosed(e);
    }
}
