using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TruckingAdvisor.AI.Streaming;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.AI.Convoy;

/// <summary>One message between convoy PCs (sent encrypted).</summary>
public sealed record ConvoyPacket
{
    /// <summary>"hello" (presence), "tx" (someone spoke), "ai" (an AI reply to play), "bye".</summary>
    [JsonPropertyName("t")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("node")] public string Node { get; init; } = string.Empty;
    /// <summary>Driver name of the PC that sent it.</summary>
    [JsonPropertyName("from")] public string From { get; init; } = string.Empty;
    [JsonPropertyName("host")] public bool Host { get; init; }
    [JsonPropertyName("ch")] public int Channel { get; init; } = 19;
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    /// <summary>tx: the AI should answer this line.</summary>
    [JsonPropertyName("ask")] public bool Ask { get; init; }
    /// <summary>tx: a Twitch viewer's line (relayed from the sender's chat).</summary>
    [JsonPropertyName("viewer")] public bool Viewer { get; init; }
    /// <summary>tx: read the line aloud in a caller voice.</summary>
    [JsonPropertyName("speak")] public bool Speak { get; init; }
    [JsonPropertyName("truck")] public TruckFacts? Truck { get; init; }
    [JsonPropertyName("speaker")] public string? Speaker { get; init; }
    [JsonPropertyName("handle")] public string? Handle { get; init; }
    [JsonPropertyName("voice")] public string? Voice { get; init; }
    [JsonPropertyName("neural")] public string? Neural { get; init; }
    [JsonPropertyName("re")] public string? ReplyTo { get; init; }
}

/// <summary>Another driver in the convoy, as last heard.</summary>
public sealed record ConvoyMember(string Node, string Name, bool ClaimsHost, int Channel, DateTimeOffset LastSeen, TruckFacts? Truck);

/// <summary>
/// Shares one AI radio between several TruckingAdvisor users in the same convoy.
///
/// - Everyone types the same convoy code; packets travel encrypted through a free ntfy relay.
/// - What a driver says on the CB (push-to-talk or typed) shows on every PC tuned to that channel.
/// - One PC, the host, generates the AI's replies and sends them out, so every PC plays the same reply.
///   If nobody ticked "host", the PC with the lowest id is host; if two did, the lowest of those.
///   If the host doesn't answer within a few seconds, the asker's own PC answers (and shares that).
/// - Twitch viewer calls are answered by the streamer's own PC and shared with the convoy.
/// </summary>
public sealed class ConvoySync : IRadioRelay, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private const int MaxTextLength = 400;

    private readonly CBRadioService _radio;
    private readonly VirtualCompanyProfile _company;
    private readonly StreamSettings _settings;
    private readonly Func<AtsTelemetrySnapshot> _telemetry;
    private readonly IConvoyTransport _transport;
    private readonly ConvoyCrypto _crypto;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, ConvoyMember> _members = new();
    private readonly Dictionary<string, (string Text, int Channel)> _pending = new();
    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _seenOrder = new();
    private DateTimeOffset _lastHello = DateTimeOffset.MinValue;
    private Task? _subscribeLoop, _presenceLoop;

    public string NodeId { get; }
    /// <summary>A member is "in the convoy" if heard from within this time (presence is sent every 5 minutes).</summary>
    public TimeSpan MemberTimeout { get; set; } = TimeSpan.FromMinutes(12);
    public TimeSpan PresenceInterval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>How long a non-host PC waits for the host's reply before answering itself.</summary>
    public TimeSpan HostReplyTimeout { get; set; } = TimeSpan.FromSeconds(12);
    public TimeSpan[] RetryDelays { get; set; } = { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) };

    public event Action<string>? StatusChanged;
    public event Action? MembersChanged;
    public bool IsConnected { get; private set; }

    public ConvoySync(CBRadioService radio, VirtualCompanyProfile company, StreamSettings settings, Func<AtsTelemetrySnapshot> telemetry,
        IConvoyTransport transport, ConvoyCrypto crypto, Func<DateTimeOffset>? clock = null, string? nodeId = null)
    {
        _radio = radio;
        _company = company;
        _settings = settings;
        _telemetry = telemetry;
        _transport = transport;
        _crypto = crypto;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        NodeId = nodeId ?? Guid.NewGuid().ToString("N")[..10];
    }

    private string MyName => _company.DisplayHandle;

    public void Start()
    {
        _radio.ChannelChanged += OnChannelChanged;
        _subscribeLoop ??= Task.Run(() => SubscribeLoopAsync(_cts.Token));
        _presenceLoop ??= Task.Run(() => PresenceLoopAsync(_cts.Token));
    }

    /// <summary>Other drivers heard from recently.</summary>
    public IReadOnlyList<ConvoyMember> Members
    {
        get
        {
            var now = _clock();
            lock (_lock) return _members.Values.Where(m => now - m.LastSeen <= MemberTimeout).OrderBy(m => m.Name).ToArray();
        }
    }

    /// <summary>True when this PC answers for the convoy's AI.</summary>
    public bool IsEffectiveHost
    {
        get
        {
            var others = Members;
            var claimers = others.Where(m => m.ClaimsHost).Select(m => m.Node).ToList();
            if (_settings.ConvoyHost) return claimers.All(n => string.CompareOrdinal(NodeId, n) < 0);
            if (claimers.Count > 0) return false;
            return others.All(m => string.CompareOrdinal(NodeId, m.Node) < 0);
        }
    }

    // ---- IRadioRelay (called by the radio) ----

    public RelayDecision OnPlayerLine(string text, int channel, out string lineId)
    {
        lineId = NewId();
        var others = Members.Where(m => m.Channel == channel).ToList();
        bool ask = ShouldAiAnswer(text, others);
        Send(new ConvoyPacket
        {
            Type = "tx", Id = lineId, Channel = channel, Name = MyName, Text = Trim(text), Ask = ask,
            Truck = TruckFacts.From(_telemetry(), MyName)
        });
        if (!ask) return RelayDecision.NoReply;
        if (others.Count == 0 || IsEffectiveHost) return RelayDecision.AnswerLocally;

        string id = lineId;
        lock (_lock) _pending[id] = (text, channel);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(HostReplyTimeout, _cts.Token); }
            catch (OperationCanceledException) { return; }
            bool stillWaiting;
            lock (_lock) stillWaiting = _pending.Remove(id);
            if (!stillWaiting) return;
            StatusChanged?.Invoke("The convoy host didn't answer, so this PC answered.");
            await _radio.AnswerCallerAsync(new CallerRequest(MyName, text, CallerRole.Trucker, CallerSource.PlayerFallback)
            {
                ShowCallerLine = false, ReplyToId = id, Channel = channel
            }, _cts.Token);
        });
        return RelayDecision.Deferred;
    }

    public void OnCallerLine(CallerRequest request, int channel)
    {
        if (Members.Count == 0) return;
        Send(new ConvoyPacket
        {
            Type = "tx", Id = NewId(), Channel = channel, Name = request.CallerName, Text = Trim(request.Text),
            Viewer = true, Speak = request.SpeakCallerLine
        });
    }

    public void OnReply(RelayedReply reply)
    {
        if (Members.Count == 0) return; // nobody to share with: save the relay's message allowance
        Send(new ConvoyPacket
        {
            Type = "ai", Id = NewId(), Channel = reply.Channel, Speaker = reply.Speaker, Handle = reply.Handle,
            Text = Trim(reply.Text), Voice = reply.VoiceId, Neural = reply.NeuralVoice, ReplyTo = reply.InReplyTo
        });
    }

    public string? ExtraContext(int channel)
    {
        var others = Members.Where(m => m.Channel == channel).ToList();
        if (others.Count == 0) return null;
        var lines = others.Select(m => "- " + (m.Truck ?? new TruckFacts { Driver = m.Name }).Describe());
        return "CONVOY: other real drivers are on this channel with " + MyName + " and hear every word. Their lines appear in the " +
               "conversation under their own names; answer whoever spoke last, by name, and when someone asks about \"my\" truck use " +
               "their numbers below.\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// The AI stays out of it when a line is clearly for another human driver ("Zay, you see that?") and names
    /// none of the radio characters. Everything else is radio talk the AI may answer.
    /// </summary>
    public bool ShouldAiAnswer(string text, IReadOnlyList<ConvoyMember> others)
    {
        if (others.Count == 0) return true;
        bool namesHuman = others.Any(m => NameMentioned(text, m.Name));
        if (!namesHuman) return true;
        bool namesRadio = _radio.Roster.Any(t => NameMentioned(text, t.Handle)) ||
                          Regex.IsMatch(text, @"\b(dispatch|shop|mechanic|safety|breaker|anybody|anyone|channel)\b", RegexOptions.IgnoreCase);
        return namesRadio;
    }

    private static bool NameMentioned(string text, string name) =>
        name.Length >= 2 && Regex.IsMatch(text, $@"(?<!\w){Regex.Escape(name)}(?!\w)", RegexOptions.IgnoreCase);

    // ---- Incoming ----

    /// <summary>Handles one raw (sealed) relay message, including any radio playback. Public for tests.</summary>
    public Task HandleRawAsync(string raw) =>
        Accept(raw) is { } accepted ? ProcessAsync(accepted.Packet, accepted.IsNew, accepted.AnswersMine) : Task.CompletedTask;

    /// <summary>
    /// Decrypts, de-duplicates and books one message right away (member list, answered questions), so a slow
    /// radio never delays the bookkeeping. Null when it isn't for us.
    /// </summary>
    private (ConvoyPacket Packet, bool IsNew, bool AnswersMine)? Accept(string raw)
    {
        if (_crypto.Open(raw) is not { } json) return null; // not our convoy (or tampered)
        ConvoyPacket? p;
        try { p = JsonSerializer.Deserialize<ConvoyPacket>(json, Json); }
        catch (JsonException) { return null; }
        if (p is null || p.Node == NodeId || p.Node.Length == 0) return null;
        bool mine;
        lock (_lock)
        {
            if (p.Id.Length > 0)
            {
                if (!_seen.Add(p.Id)) return null;
                _seenOrder.Enqueue(p.Id);
                if (_seenOrder.Count > 500) _seen.Remove(_seenOrder.Dequeue());
            }
            mine = p.Type == "ai" && p.ReplyTo is not null && _pending.Remove(p.ReplyTo);
        }
        return (p, TrackMember(p), mine);
    }

    private async Task ProcessAsync(ConvoyPacket p, bool isNew, bool answersMine)
    {
        switch (p.Type)
        {
            case "hello":
                if (isNew)
                {
                    StatusChanged?.Invoke($"{p.From} joined the convoy.");
                    SendHello(force: false); // let the newcomer know we're here
                }
                break;

            case "bye":
                lock (_lock) _members.Remove(p.Node);
                MembersChanged?.Invoke();
                StatusChanged?.Invoke($"{p.From} left the convoy.");
                break;

            case "tx" when !string.IsNullOrWhiteSpace(p.Text):
            {
                if (p.Channel != _radio.ActiveChannel) break; // different CB channel: not heard
                string name = Trim(p.Name ?? p.From, 40);
                string text = Trim(p.Text);
                if (p.Viewer)
                {
                    await _radio.PlayRelayedLineAsync($"{name} (Twitch chat, {p.From}'s stream)", name, text, p.Channel,
                        speak: p.Speak, cancellationToken: _cts.Token);
                }
                else if (p.Ask && IsEffectiveHost)
                {
                    var role = RoleFor(text);
                    await _radio.AnswerCallerAsync(new CallerRequest(name, text, role, CallerSource.Convoy)
                    {
                        SpokenName = ViewerCallGate.SpeakableName(name),
                        CallerTruck = p.Truck,
                        SpeakCallerLine = _settings.ReadConvoyLinesAloud,
                        ReplyToId = p.Id,
                        Channel = p.Channel
                    }, _cts.Token);
                }
                else
                {
                    await _radio.PlayRelayedLineAsync($"{name} (convoy)", name, text, p.Channel,
                        speak: _settings.ReadConvoyLinesAloud, cancellationToken: _cts.Token);
                }
                break;
            }

            case "ai" when !string.IsNullOrWhiteSpace(p.Text):
            {
                if (!answersMine && p.Channel != _radio.ActiveChannel && p.Channel != CBRadioService.DispatchChannel) break;
                await _radio.PlayRelayedLineAsync(Trim(p.Speaker ?? p.Handle ?? "Radio", 60), Trim(p.Handle ?? "Radio", 40), Trim(p.Text),
                    p.Channel, speak: true, voiceId: p.Voice, neuralVoice: p.Neural, cancellationToken: _cts.Token);
                break;
            }
        }
    }

    private static CallerRole RoleFor(string text)
    {
        if (Regex.IsMatch(text, @"\b(mechanic|shop)\b", RegexOptions.IgnoreCase)) return CallerRole.Mechanic;
        if (Regex.IsMatch(text, @"\bdispatch", RegexOptions.IgnoreCase)) return CallerRole.Dispatch;
        if (Regex.IsMatch(text, @"\bsafety\b", RegexOptions.IgnoreCase)) return CallerRole.Safety;
        return CallerRole.Trucker;
    }

    private bool TrackMember(ConvoyPacket p)
    {
        bool isNew;
        lock (_lock)
        {
            isNew = !_members.TryGetValue(p.Node, out var old) || _clock() - old.LastSeen > MemberTimeout;
            var truck = p.Type is "hello" or "tx" && !p.Viewer ? p.Truck ?? old?.Truck : old?.Truck;
            // Viewer lines and AI replies carry the listener's channel too.
            _members[p.Node] = new ConvoyMember(p.Node, Trim(p.From, 40), p.Host, p.Channel, _clock(), truck);
        }
        if (isNew) MembersChanged?.Invoke();
        return isNew;
    }

    private async Task SubscribeLoopAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke("Joining the convoy...");
                // Playback runs one message after another on its own chain, so reading never waits for the radio.
                Task chain = Task.CompletedTask;
                await foreach (var raw in _transport.SubscribeAsync(ct))
                {
                    failures = 0;
                    if (Accept(raw) is not { } a) continue;
                    chain = chain.ContinueWith(async _ =>
                    {
                        try { await ProcessAsync(a.Packet, a.IsNew, a.AnswersMine); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { StatusChanged?.Invoke($"Convoy message problem: {ex.Message}"); }
                    }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { StatusChanged?.Invoke($"Convoy connection problem: {ex.Message}"); }
            IsConnected = false;
            if (ct.IsCancellationRequested) break;
            var delay = RetryDelays[Math.Min(failures++, RetryDelays.Length - 1)];
            StatusChanged?.Invoke($"Convoy disconnected. Reconnecting in {delay.TotalSeconds:F0} s...");
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task PresenceLoopAsync(CancellationToken ct)
    {
        // Give the subscription a moment to open so the first hello's answers aren't missed.
        try { await Task.Delay(TimeSpan.FromSeconds(1.5), ct); } catch (OperationCanceledException) { return; }
        IsConnected = true;
        StatusChanged?.Invoke($"In the convoy as {MyName}. Waiting for other drivers...");
        while (!ct.IsCancellationRequested)
        {
            SendHello(force: true);
            try { await Task.Delay(PresenceInterval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Tell the convoy which channel we're on now, once the knob settles.</summary>
    private void OnChannelChanged(object? sender, int channel)
    {
        int stamp = Interlocked.Increment(ref _channelStamp);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token); } catch (OperationCanceledException) { return; }
            if (stamp == Volatile.Read(ref _channelStamp) && Members.Count > 0) SendHello(force: true);
        });
    }

    private int _channelStamp;

    private void SendHello(bool force)
    {
        var now = _clock();
        lock (_lock)
        {
            if (!force && now - _lastHello < TimeSpan.FromSeconds(20)) return;
            _lastHello = now;
        }
        Send(new ConvoyPacket { Type = "hello", Id = NewId(), Channel = _radio.ActiveChannel, Truck = TruckFacts.From(_telemetry(), MyName) });
    }

    private void Send(ConvoyPacket packet)
    {
        var full = packet with { Node = NodeId, From = MyName, Host = _settings.ConvoyHost };
        string sealedText = _crypto.Seal(JsonSerializer.Serialize(full, Json));
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await _transport.PublishAsync(sealedText, timeout.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { StatusChanged?.Invoke(ex is ConvoyRelayException ? ex.Message : $"Couldn't reach the convoy: {ex.Message}"); }
        });
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static string Trim(string? s, int max = MaxTextLength)
    {
        string t = (s ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..max];
    }

    public async ValueTask DisposeAsync()
    {
        _radio.ChannelChanged -= OnChannelChanged;
        if (!_cts.IsCancellationRequested)
        {
            try
            {
                var bye = new ConvoyPacket { Type = "bye", Id = NewId(), Node = NodeId, From = MyName, Channel = _radio.ActiveChannel };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _transport.PublishAsync(_crypto.Seal(JsonSerializer.Serialize(bye, Json)), timeout.Token);
            }
            catch { /* leaving anyway */ }
        }
        _cts.Cancel();
        foreach (var t in new[] { _subscribeLoop, _presenceLoop })
            if (t is not null) { try { await t.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
        _cts.Dispose();
    }
}
