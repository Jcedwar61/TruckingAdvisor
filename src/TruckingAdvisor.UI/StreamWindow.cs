using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TruckingAdvisor.AI;
using TruckingAdvisor.AI.Streaming;

namespace TruckingAdvisor.UI;

/// <summary>
/// Twitch, Overlay & Convoy: lets Twitch chat talk to the AI truckers, serves the OBS overlay, and links
/// TruckingAdvisor users into a shared convoy radio. Changes are applied when "Save & apply" is clicked.
/// </summary>
public sealed class StreamWindow : Window
{
    private readonly AiSettings _settings;
    private readonly StreamHub _hub;
    private readonly Action _save;
    private StreamSettings S => _settings.Stream;

    // Twitch
    private readonly CheckBox _twitchOn = new() { Content = "Let Twitch chat talk on the CB radio" };
    private readonly TextBox _twitchChannel = new();
    private readonly TextBox _globalCooldown = new() { Width = 70 };
    private readonly TextBox _userCooldown = new() { Width = 70 };
    private readonly CheckBox _subsOnly = new() { Content = "Subscribers, VIPs and mods only" };
    private readonly CheckBox _readAloud = new() { Content = "Read the viewer's message aloud first (in a caller voice)" };
    private readonly CheckBox _allowDispatch = new() { Content = "!dispatch" };
    private readonly CheckBox _allowMechanic = new() { Content = "!mechanic" };
    private readonly CheckBox _allowSafety = new() { Content = "!safety" };
    private readonly CheckBox _pointsOn = new() { Content = "A Channel Points reward with a message keys up too (skips the cooldown)" };
    private readonly TextBox _rewardName = new();
    private readonly TextBox _rewardId = new();
    private readonly TextBox _blocked = new();
    private readonly TextBox _testText = new() { Text = "!cb radio check, how's it looking out there?" };
    private readonly TextBlock _twitchStatus = Status();

    // Overlay
    private readonly CheckBox _overlayOn = new() { Content = "Run the stream overlay (OBS / Streamlabs Browser Source)" };
    private readonly TextBox _overlayPort = new() { Width = 70 };
    private readonly CheckBox _overlayLan = new() { Content = "Allow other PCs on my network (two-PC streaming setup)" };
    private readonly TextBlock _overlayStatus = Status();

    // Convoy
    private readonly CheckBox _convoyOn = new() { Content = "Join a convoy: share the AI radio with friends who use TruckingAdvisor" };
    private readonly PasswordBox _convoyCode = new();
    private readonly CheckBox _convoyHost = new() { Content = "This PC answers for the convoy's AI (tick on one PC; if nobody does, one is picked)" };
    private readonly CheckBox _convoyRead = new() { Content = "Read the other drivers' lines aloud (off if you already hear them on voice chat)" };
    private readonly TextBox _convoyServer = new();
    private readonly TextBlock _convoyStatus = Status();

    private readonly ListBox _activity = new() { Height = 110, FontSize = 11 };

    public StreamWindow(AiSettings settings, StreamHub hub, Action save)
    {
        _settings = settings;
        _hub = hub;
        _save = save;
        Title = "Twitch, Overlay & Convoy";
        Width = 760;
        Height = 860;
        MinWidth = 600;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var apply = MakeButton("Save & apply", async (_, _) => await ApplyAsync());
        apply.SetResourceReference(BackgroundProperty, "AccentBrush");
        apply.SetResourceReference(ForegroundProperty, "BgBrush");
        buttons.Children.Add(apply);
        buttons.Children.Add(MakeButton("Close", (_, _) => Close()));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var body = new StackPanel();
        body.Children.Add(Intro(
            "Viewers type commands like !cb in your Twitch chat and an AI trucker answers on your CB, out loud, using your live truck data. " +
            "No Twitch login or token is needed: chat is only read, never written to. With AI switched on in Settings, replies come from your AI " +
            "(OpenAI, or any OpenAI-compatible service such as Groq or a local Ollama); with AI off, built-in replies still use your real fuel, speed, load and wear."));

        // ---- Twitch ----
        body.Children.Add(Header("TWITCH CHAT"));
        body.Children.Add(_twitchOn);
        body.Children.Add(Labeled("Your Twitch channel", _twitchChannel, "Your channel name, e.g. highwayghost (a twitch.tv link works too)."));
        var cool = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        cool.Children.Add(Small("Seconds between calls"));
        cool.Children.Add(_globalCooldown);
        cool.Children.Add(Small("   Seconds per viewer"));
        cool.Children.Add(_userCooldown);
        body.Children.Add(cool);
        body.Children.Add(_subsOnly);
        body.Children.Add(_readAloud);
        var cmds = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        cmds.Children.Add(Small("Crew commands (!cb is always on):  "));
        foreach (var c in new[] { _allowDispatch, _allowMechanic, _allowSafety }) { c.Margin = new Thickness(0, 0, 14, 0); cmds.Children.Add(c); }
        body.Children.Add(cmds);
        body.Children.Add(_pointsOn);
        body.Children.Add(Labeled("Reward name (shown on the overlay)", _rewardName, "Create a Channel Points reward with \"Require viewer to enter text\" ticked, and give it this name."));
        body.Children.Add(Labeled("Only this reward id (optional)", _rewardId, "Leave empty to accept any reward that has a message. The id is shown in the activity log below when a reward is redeemed."));
        body.Children.Add(Labeled("Ignore messages with these words", _blocked, "Comma separated. Twitch AutoMod also applies before messages reach the app."));
        var test = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var testBtn = MakeButton("Play a test call", (_, _) =>
        {
            if (!_hub.SimulateViewer("TestViewer", _testText.Text)) Log("A test call is already waiting.");
            else Log("Test call queued (cooldowns skipped).");
        });
        DockPanel.SetDock(testBtn, Dock.Right);
        test.Children.Add(testBtn);
        test.Children.Add(_testText);
        body.Children.Add(test);
        body.Children.Add(_twitchStatus);

        // ---- Overlay ----
        body.Children.Add(Header("STREAM OVERLAY"));
        body.Children.Add(_overlayOn);
        var port = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        port.Children.Add(Small("Port"));
        port.Children.Add(_overlayPort);
        body.Children.Add(port);
        body.Children.Add(_overlayLan);
        var urlRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        urlRow.Children.Add(MakeButton("Copy overlay URL", (_, _) => CopyUrl("/overlay")));
        urlRow.Children.Add(MakeButton("Open in browser", (_, _) => OpenUrl("/overlay")));
        urlRow.Children.Add(MakeButton("Preview with sample data", (_, _) => OpenUrl("/overlay?demo=1")));
        body.Children.Add(urlRow);
        body.Children.Add(Note(
            "In OBS: Sources > + > Browser, paste the URL, size 1920 x 1080. One source shows the commands panel and the caller banner; " +
            "for separate sources use /overlay/commands, /overlay/banner or /overlay/feed. Add ?pos=br (or tl, tr) to move the panel, " +
            "?mode=ticker for a compact rotating strip, ?scale=1.3 to resize. The overlay never shows your convoy code."));
        body.Children.Add(_overlayStatus);

        // ---- Convoy ----
        body.Children.Add(Header("CONVOY CB"));
        body.Children.Add(Note(
            "Friends who also run TruckingAdvisor type the same convoy code. What anyone says on the CB (push-to-talk or typed) shows on " +
            "everyone's radio on that channel, and one shared AI answers so all of you hear the same reply. Your viewers' calls are shared too. " +
            "Messages are encrypted with the code and pass through a free relay (ntfy.sh); it allows a few hundred messages a day per PC."));
        body.Children.Add(_convoyOn);
        body.Children.Add(Labeled("Convoy code", _convoyCode, "At least 4 letters or digits, e.g. ROADDOGS-7740. Keep it off stream."));
        body.Children.Add(_convoyHost);
        body.Children.Add(_convoyRead);
        body.Children.Add(Labeled("Relay server", _convoyServer, "https://ntfy.sh, or your own ntfy server. Everyone must use the same one."));
        body.Children.Add(_convoyStatus);

        body.Children.Add(Header("ACTIVITY"));
        _activity.SetResourceReference(BackgroundProperty, "BgBrush");
        _activity.SetResourceReference(ForegroundProperty, "TextBrush");
        body.Children.Add(_activity);

        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;

        Load();
        // Turning on Twitch for the first time also turns on the overlay that lists the commands.
        _twitchOn.Checked += (_, _) => { if (!S.TwitchEnabled && !S.OverlayEnabled) _overlayOn.IsChecked = true; };
        RefreshStatus();
        _hub.StatusChanged += OnHubStatus;
        _hub.Activity += OnActivity;
        Closed += (_, _) =>
        {
            _hub.StatusChanged -= OnHubStatus;
            _hub.Activity -= OnActivity;
        };
    }

    private void OnHubStatus() => Dispatcher.BeginInvoke(RefreshStatus);
    private void OnActivity(string line) => Dispatcher.BeginInvoke(() => Log(line));

    private void Load()
    {
        _twitchOn.IsChecked = S.TwitchEnabled;
        _twitchChannel.Text = S.TwitchChannel;
        _globalCooldown.Text = S.GlobalCooldownSeconds.ToString();
        _userCooldown.Text = S.UserCooldownSeconds.ToString();
        _subsOnly.IsChecked = S.SubscribersOnly;
        _readAloud.IsChecked = S.ReadViewerMessageAloud;
        _allowDispatch.IsChecked = S.AllowDispatch;
        _allowMechanic.IsChecked = S.AllowMechanic;
        _allowSafety.IsChecked = S.AllowSafety;
        _pointsOn.IsChecked = S.ChannelPointsEnabled;
        _rewardName.Text = S.ChannelPointsRewardName;
        _rewardId.Text = S.ChannelPointsRewardId;
        _blocked.Text = S.BlockedWords;
        _overlayOn.IsChecked = S.OverlayEnabled;
        _overlayPort.Text = S.OverlayPort.ToString();
        _overlayLan.IsChecked = S.OverlayAllowLan;
        _convoyOn.IsChecked = S.ConvoyEnabled;
        _convoyCode.Password = S.ConvoyCode;
        _convoyHost.IsChecked = S.ConvoyHost;
        _convoyRead.IsChecked = S.ReadConvoyLinesAloud;
        _convoyServer.Text = S.ConvoyServer;
    }

    private async Task ApplyAsync()
    {
        S.TwitchEnabled = _twitchOn.IsChecked == true;
        S.TwitchChannel = StreamSettings.NormalizeChannel(_twitchChannel.Text);
        _twitchChannel.Text = S.TwitchChannel;
        S.GlobalCooldownSeconds = ParseInt(_globalCooldown.Text, S.GlobalCooldownSeconds, 0, 3600);
        S.UserCooldownSeconds = ParseInt(_userCooldown.Text, S.UserCooldownSeconds, 0, 86400);
        S.SubscribersOnly = _subsOnly.IsChecked == true;
        S.ReadViewerMessageAloud = _readAloud.IsChecked == true;
        S.AllowDispatch = _allowDispatch.IsChecked == true;
        S.AllowMechanic = _allowMechanic.IsChecked == true;
        S.AllowSafety = _allowSafety.IsChecked == true;
        S.ChannelPointsEnabled = _pointsOn.IsChecked == true;
        S.ChannelPointsRewardName = _rewardName.Text.Trim();
        S.ChannelPointsRewardId = _rewardId.Text.Trim();
        S.BlockedWords = _blocked.Text.Trim();
        S.OverlayEnabled = _overlayOn.IsChecked == true;
        S.OverlayPort = ParseInt(_overlayPort.Text, S.OverlayPort, 1024, 65535);
        S.OverlayAllowLan = _overlayLan.IsChecked == true;
        S.ConvoyEnabled = _convoyOn.IsChecked == true;
        S.ConvoyCode = _convoyCode.Password.Trim();
        S.ConvoyHost = _convoyHost.IsChecked == true;
        S.ReadConvoyLinesAloud = _convoyRead.IsChecked == true;
        S.ConvoyServer = string.IsNullOrWhiteSpace(_convoyServer.Text) ? "https://ntfy.sh" : _convoyServer.Text.Trim();
        Load();
        try { _save(); }
        catch (Exception ex) { Log($"Couldn't save settings: {ex.Message}"); }
        await _hub.ApplyAsync();
        RefreshStatus();
        Log("Settings applied.");
    }

    private void RefreshStatus()
    {
        _twitchStatus.Text = _hub.TwitchStatus;
        _overlayStatus.Text = _hub.OverlayStatus;
        _convoyStatus.Text = _hub.ConvoyStatus;
    }

    private void Log(string line)
    {
        _activity.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        while (_activity.Items.Count > 60) _activity.Items.RemoveAt(_activity.Items.Count - 1);
    }

    private string BaseUrl => _hub.OverlayUrl is { } u ? u[..u.LastIndexOf('/')] : $"http://localhost:{S.OverlayPort}";

    private void CopyUrl(string path)
    {
        try
        {
            Clipboard.SetText(BaseUrl + path);
            Log($"Copied {BaseUrl + path}");
        }
        catch (Exception ex) { Log($"Couldn't copy: {ex.Message}"); }
        if (_hub.OverlayUrl is null) Log("The overlay isn't running yet: tick it and click Save & apply.");
    }

    private void OpenUrl(string path)
    {
        if (_hub.OverlayUrl is null && !path.Contains("demo")) { Log("The overlay isn't running yet: tick it and click Save & apply."); return; }
        try { Process.Start(new ProcessStartInfo(BaseUrl + path) { UseShellExecute = true }); }
        catch (Exception ex) { Log($"Couldn't open the browser: {ex.Message}"); }
    }

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text.Trim(), out int v) ? Math.Clamp(v, min, max) : fallback;

    // ---- small UI helpers (styled by the app theme) ----

    private static TextBlock Status()
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        return t;
    }

    private static TextBlock Header(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(0, 16, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        return t;
    }

    private static TextBlock Intro(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };

    private static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    private static TextBlock Small(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

    private static FrameworkElement Labeled(string label, Control input, string? hint = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        panel.Children.Add(input);
        if (hint is not null)
        {
            var h = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
            h.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(h);
        }
        return panel;
    }

    private static Button MakeButton(string text, RoutedEventHandler? onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4) };
        if (onClick is not null) b.Click += onClick;
        return b;
    }
}
