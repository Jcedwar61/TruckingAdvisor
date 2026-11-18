using System.Threading.Channels;
using TruckingAdvisor.AI.Convoy;
using TruckingAdvisor.Shared.Diagnostics;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.AI.Streaming;

/// <summary>
/// Runs the streaming features from the settings: Twitch chat -> radio calls, the convoy link and the OBS
/// overlay. Call <see cref="ApplyAsync"/> after the settings change; it starts, restarts or stops each part.
/// </summary>
public sealed class StreamHub : IAsyncDisposable
{
    private readonly CBRadioService _radio;
    private readonly AiSettings _settings;
    private readonly VirtualCompanyProfile _company;
    private readonly Func<AtsTelemetrySnapshot> _telemetry;
    private readonly Func<string, TwitchChatClient> _twitchFactory;
    private readonly Func<StreamSettings, string, IConvoyTransport> _transportFactory;
    private readonly ViewerCallGate _gate;
    private readonly Channel<CallerRequest> _calls = Channel.CreateBounded<CallerRequest>(
        new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly Queue<string> _announced = new();
    private readonly Task _worker;
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private TwitchChatClient? _twitch;
    private string _twitchKey = string.Empty;
    private StreamOverlayServer? _overlay;
    private string _overlayKey = string.Empty;
    private ConvoySync? _convoy;
    private string _convoyKey = string.Empty;
    private bool _wasOnAir;

    public string TwitchStatus { get; private set; } = "Twitch chat is off.";
    public string ConvoyStatus { get; private set; } = "Convoy is off.";
    public string OverlayStatus { get; private set; } = "Overlay is off.";
    public string? OverlayUrl => _overlay?.Url;
    public ConvoySync? Convoy => _convoy;
    public ViewerCallGate Gate => _gate;
    /// <summary>Raised (on a background thread) when any status text changes.</summary>
    public event Action? StatusChanged;
    /// <summary>Viewer messages that were accepted or ignored, for the settings window's log.</summary>
    public event Action<string>? Activity;

    public StreamHub(CBRadioService radio, AiSettings settings, VirtualCompanyProfile company, Func<AtsTelemetrySnapshot> telemetry,
        Func<string, TwitchChatClient>? twitchFactory = null, Func<StreamSettings, string, IConvoyTransport>? transportFactory = null)
    {
        _radio = radio;
        _settings = settings;
        _company = company;
        _telemetry = telemetry;
        _twitchFactory = twitchFactory ?? (ch => new TwitchChatClient(ch));
        _transportFactory = transportFactory ?? ((s, topic) => new NtfyConvoyTransport(SharedHttp, s.ConvoyServer, topic));
        _gate = new ViewerCallGate(settings.Stream);

        _radio.CallerKeyedUp += OnCallerKeyedUp;
        _radio.CallerAnswered += OnCallerAnswered;
        _radio.TransmissionReceived += OnTransmission;
        _radio.StateChanged += OnRadioState;
        _radio.ChannelChanged += OnChannelChanged;
        _worker = Task.Run(() => WorkerAsync(_cts.Token));
    }

    private StreamSettings S => _settings.Stream;

    /// <summary>Starts, restarts or stops Twitch, the overlay and the convoy to match the settings.</summary>
    public async Task ApplyAsync()
    {
        await _applyLock.WaitAsync();
        try
        {
            await ApplyOverlayAsync();
            await ApplyTwitchAsync();
            await ApplyConvoyAsync();
        }
        finally
        {
            _applyLock.Release();
        }
        PushState();
    }

    private async Task ApplyOverlayAsync()
    {
        string key = S.OverlayEnabled ? $"{S.OverlayPort}|{S.OverlayAllowLan}" : string.Empty;
        if (key == _overlayKey) return;
        if (_overlay is not null) { await _overlay.DisposeAsync(); _overlay = null; }
        _overlayKey = key;
        if (key.Length == 0) { SetOverlayStatus("Overlay is off."); return; }
        try
        {
            var server = new StreamOverlayServer(S.OverlayPort, BuildOverlayState, S.OverlayAllowLan);
            server.Start();
            _overlay = server;
            SetOverlayStatus($"Overlay ready: add {server.Url} as a Browser Source in OBS.");
        }
        catch (Exception ex)
        {
            _overlayKey = string.Empty;
            SetOverlayStatus($"Overlay couldn't start on port {S.OverlayPort} ({ex.Message}). Pick another port.");
        }
    }

    private async Task ApplyTwitchAsync()
    {
        string channel = StreamSettings.NormalizeChannel(S.TwitchChannel);
        string key = S.TwitchEnabled && channel.Length > 0 ? channel : string.Empty;
        if (key == _twitchKey) return;
        if (_twitch is not null)
        {
            _twitch.MessageReceived -= OnChatMessage;
            await _twitch.DisposeAsync();
            _twitch = null;
        }
        _twitchKey = key;
        if (key.Length == 0)
        {
            SetTwitchStatus(S.TwitchEnabled ? "Type your Twitch channel name to connect." : "Twitch chat is off.");
            return;
        }
        var client = _twitchFactory(channel);
        client.MessageReceived += OnChatMessage;
        client.StatusChanged += msg => { SetTwitchStatus(msg); PushState(); };
        _twitch = client;
        client.Start();
    }

    private async Task ApplyConvoyAsync()
    {
        string code = ConvoyCrypto.NormalizeCode(S.ConvoyCode);
        string key = S.ConvoyEnabled && code.Length >= 4 ? $"{code}|{S.ConvoyServer.Trim()}" : string.Empty;
        if (key == _convoyKey) return;
        if (_convoy is not null)
        {
            _radio.Relay = null;
            await _convoy.DisposeAsync();
            _convoy = null;
        }
        _convoyKey = key;
        if (key.Length == 0)
        {
            SetConvoyStatus(S.ConvoyEnabled ? "Type a convoy code (at least 4 letters or digits) - everyone in the convoy uses the same one." : "Convoy is off.");
            return;
        }
        var crypto = new ConvoyCrypto(code);
        var sync = new ConvoySync(_radio, _company, S, _telemetry, _transportFactory(S, crypto.Topic), crypto);
        sync.StatusChanged += msg => { SetConvoyStatus(msg); };
        sync.MembersChanged += () => { SetConvoyStatus(ConvoySummary(sync)); PushState(); };
        _convoy = sync;
        _radio.Relay = sync;
        sync.Start();
    }

    private static string ConvoySummary(ConvoySync sync)
    {
        var names = sync.Members.Select(m => m.Name).ToList();
        string host = sync.IsEffectiveHost ? "This PC answers for the convoy's AI." : "Another PC answers for the convoy's AI.";
        return names.Count == 0 ? "In the convoy. Waiting for other drivers..." : $"In the convoy with {string.Join(", ", names)}. {host}";
    }

    // ---- Twitch -> radio ----

    /// <summary>Reports to the Stream window and to app.log (so a test on another PC can be diagnosed later).</summary>
    private void Report(string line)
    {
        AppLog.Info($"Stream: {line}");
        Activity?.Invoke(line);
    }

    private void OnChatMessage(TwitchChatMessage message)
    {
        if (message.CustomRewardId is { } rewardId)
            Report($"{message.DisplayName} redeemed a Channel Points reward (reward id {rewardId}).");
        var call = _gate.TryAccept(message, out string? reason);
        if (call is null)
        {
            if (reason is not "not a command") Report($"Ignored {message.DisplayName}: {reason}.");
            return;
        }
        if (!Enqueue(call)) Report($"Ignored {message.DisplayName}: too many calls waiting.");
        else Report($"{message.DisplayName} is on the air: \"{call.Text}\"");
        PushState();
    }

    /// <summary>Queues a viewer call (max 3 waiting). False when the queue is full.</summary>
    public bool Enqueue(ViewerCall call) => _calls.Writer.TryWrite(new CallerRequest(call.DisplayName, call.Text, call.Role, CallerSource.Twitch)
    {
        SpokenName = ViewerCallGate.SpeakableName(call.DisplayName),
        SpeakCallerLine = S.ReadViewerMessageAloud
    });

    /// <summary>Plays a pretend viewer call (Settings > Test), skipping cooldowns, to check audio and the overlay.</summary>
    public bool SimulateViewer(string displayName, string text)
    {
        var parsed = ViewerCallGate.ParseCommand(text);
        var role = parsed?.Role ?? CallerRole.Trucker;
        string body = ViewerCallGate.Clean(parsed?.Body ?? text);
        if (body.Length == 0) body = "Radio check, how's it looking out there?";
        return Enqueue(new ViewerCall(displayName.ToLowerInvariant(), displayName, body, role, false));
    }

    /// <summary>Longest one call may take (wait + reply + speech) before the next one gets its turn.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(3);

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in _calls.Reader.ReadAllAsync(ct))
            {
                // One stuck call (a voice that never finishes playing, a hung request) must not block every later one.
                using var perCall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                perCall.CancelAfter(CallTimeout);
                bool ok;
                try { ok = await _radio.AnswerCallerAsync(request, perCall.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { ok = false; }
                catch (Exception ex) { AppLog.Error("Stream: answering a caller failed", ex); ok = false; }
                if (!ok)
                    Report($"Couldn't put {request.CallerName} on the air: {_radio.LastCallerProblem ?? "the radio was busy or off"}.");
                else
                    Report($"{request.CallerName} was answered on the radio.");
                PushState();
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---- Radio -> overlay ----

    private static string CommandFor(CallerRole role) => ViewerCallGate.AllCommands.First(c => c.Role == role).Command;

    private void Remember(string text)
    {
        lock (_announced)
        {
            _announced.Enqueue(text);
            while (_announced.Count > 10) _announced.Dequeue();
        }
    }

    private void OnCallerKeyedUp(object? sender, (CallerRequest Request, int Channel) e)
    {
        Remember(e.Request.Text);
        _overlay?.PushLine(new
        {
            kind = "caller",
            source = e.Request.Source == CallerSource.Twitch ? "twitch" : "convoy",
            command = e.Request.Source == CallerSource.Twitch ? CommandFor(e.Request.Role) : null,
            handle = e.Request.CallerName,
            text = e.Request.Text,
            channel = e.Channel
        });
    }

    private void OnCallerAnswered(object? sender, (CallerRequest Request, string Responder, string Reply, int Channel) e)
    {
        Remember(e.Reply);
        _overlay?.PushLine(new { kind = "reply", handle = e.Responder, text = e.Reply, channel = e.Channel });
    }

    private void OnTransmission(object? sender, (string Speaker, string Handle, string Transcript, int Channel) e)
    {
        lock (_announced) if (_announced.Contains(e.Transcript)) return; // already shown as a caller or reply
        _overlay?.PushLine(new { kind = "line", handle = e.Handle, text = e.Transcript, channel = e.Channel });
    }

    private void OnChannelChanged(object? sender, int channel) => PushState();

    private void OnRadioState(object? sender, CBRadioState state)
    {
        bool onAir = state is CBRadioState.PlayingAiResponse or CBRadioState.PlayingTransmission or CBRadioState.IncomingTransmission;
        if (onAir == _wasOnAir) return;
        _wasOnAir = onAir;
        PushState();
    }

    /// <summary>Everything the overlay shows. Never includes the convoy code (it would let stream viewers join).</summary>
    public object BuildOverlayState() => new
    {
        streamer = _company.DisplayHandle,
        channel = _radio.ActiveChannel is CBRadioService.DispatchChannel or CBRadioService.EmergencyChannel ? 19 : _radio.ActiveChannel,
        radio = _wasOnAir ? "rx" : "idle",
        ai = _radio.AiAvailable,
        twitch = new
        {
            enabled = S.TwitchEnabled,
            connected = _twitch?.IsConnected == true,
            channel = _twitchKey,
            cooldownRemaining = _gate.SecondsUntilOpen(),
            globalCooldown = Math.Max(0, S.GlobalCooldownSeconds),
            userCooldown = Math.Max(0, S.UserCooldownSeconds),
            subsOnly = S.SubscribersOnly
        },
        commands = _gate.EnabledCommands().Select(c => new { command = c.Command, aliases = c.Aliases, description = c.Description, example = c.Example }),
        channelPoints = new { enabled = S.ChannelPointsEnabled, reward = S.ChannelPointsRewardName },
        convoy = new { active = _convoy is not null, members = _convoy?.Members.Select(m => m.Name).ToArray() ?? Array.Empty<string>() }
    };

    /// <summary>Re-sends the overlay state (e.g. after the CB channel changed).</summary>
    public void PushState() => _overlay?.PushState();

    private void SetTwitchStatus(string s) { if (s != TwitchStatus) AppLog.Info($"Stream: {s}"); TwitchStatus = s; StatusChanged?.Invoke(); }
    private void SetConvoyStatus(string s) { if (s != ConvoyStatus) AppLog.Info($"Stream: {s}"); ConvoyStatus = s; StatusChanged?.Invoke(); }
    private void SetOverlayStatus(string s) { if (s != OverlayStatus) AppLog.Info($"Stream: {s}"); OverlayStatus = s; StatusChanged?.Invoke(); }

    public async ValueTask DisposeAsync()
    {
        _radio.CallerKeyedUp -= OnCallerKeyedUp;
        _radio.CallerAnswered -= OnCallerAnswered;
        _radio.TransmissionReceived -= OnTransmission;
        _radio.StateChanged -= OnRadioState;
        _radio.ChannelChanged -= OnChannelChanged;
        _cts.Cancel();
        _calls.Writer.TryComplete();
        if (_twitch is not null) await _twitch.DisposeAsync();
        if (_convoy is not null) { _radio.Relay = null; await _convoy.DisposeAsync(); }
        if (_overlay is not null) await _overlay.DisposeAsync();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
