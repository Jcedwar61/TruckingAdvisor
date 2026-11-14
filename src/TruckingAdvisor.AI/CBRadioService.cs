using System.Text.Json;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.AI;

/// <summary>
/// Half-duplex CB radio:
/// RadioIdle -> PlayerTransmitting -> ProcessingSpeech -> GeneratingResponse -> PlayingAiResponse -> RadioIdle.
///
/// - Uses the injected AI services when available; otherwise answers with short offline lines,
///   so the radio keeps working with AI off.
/// - All work is async and never touches the telemetry thread; only one exchange runs at a time.
/// - Speech goes through the shared audio priority manager, so safety alerts always win.
/// - Truckers are simulated radio voices; nothing here claims to detect real nearby vehicles.
/// </summary>
public sealed class CBRadioService : ICBRadioService
{
    public const int DispatchChannel = 30;
    public const int EmergencyChannel = 9;

    private readonly List<TruckerPersonality> _roster;
    private readonly VirtualCompanyProfile _company;
    private readonly IAudioPriorityManager _audio;
    private readonly ILlmClient? _llm;
    private readonly ISpeechToText? _stt;
    private readonly ITextToSpeech? _tts;
    private readonly Func<AtsTelemetrySnapshot> _telemetry;
    private readonly AiSettings _settings;
    private readonly Random _random;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<RadioTurn> _recent = new();
    private CancellationTokenSource _chatterCts = new();
    private readonly object _stateLock = new();
    /// <summary>Bumped on every player key-up; older exchanges and chatter must not touch newer state.</summary>
    private int _txVersion;
    private readonly Queue<DateTimeOffset> _unsolicited = new();

    public event EventHandler<CBRadioState>? StateChanged;
    public event EventHandler<(string Speaker, string Handle, string Transcript, int Channel)>? TransmissionReceived;
    /// <summary>Raised after a trucker's relationship/memory changed, so it can be saved.</summary>
    public event EventHandler<TruckerPersonality>? TruckerUpdated;
    /// <summary>Short status for the UI ("AI offline", "No speech heard", ...).</summary>
    public event EventHandler<string>? Notice;
    /// <summary>The player addressed the app ("TruckingAdvisor, ..."); handled outside the radio.</summary>
    public event EventHandler<VoiceCommand>? CommandReceived;
    /// <summary>The player introduced a new CB handle; the UI should confirm before saving it.</summary>
    public event EventHandler<string>? HandleChangeProposed;

    public CBRadioState CurrentState { get; private set; } = CBRadioState.RadioIdle;
    public int ActiveChannel { get; private set; } = 19;
    public HalfDuplexCollisionPolicy CollisionPolicy { get; set; } = HalfDuplexCollisionPolicy.InterruptSpeaker;
    public TruckerPersonality? ActivePartner { get; private set; }
    public IReadOnlyList<TruckerPersonality> Roster { get { lock (_roster) return _roster.ToArray(); } }
    public bool AiAvailable => _llm?.IsAvailable == true;
    /// <summary>How long the truck has been parked (set by the dashboard); enables truck-stop talk.</summary>
    public TimeSpan ParkedFor { get; set; }
    /// <summary>Random pause before an AI reply, so it doesn't answer instantly like a machine.</summary>
    public (int MinMs, int MaxMs) ReplyPause { get; set; } = (350, 1200);
    /// <summary>Opens a realtime connection (WebSocket in the app, fake in tests). Null disables realtime voice.</summary>
    public Func<IRealtimeTransport>? RealtimeTransportFactory { get; set; }
    public Func<string?>? RealtimeApiKey { get; set; }
    public AiUsageTracker? RealtimeUsage { get; set; }

    private bool RealtimeReady =>
        _settings.VoicePipeline == VoicePipeline.Realtime && _settings.GlobalAiEnabled && _settings.Provider == AiLlmProvider.OpenAI &&
        RealtimeTransportFactory is not null && RealtimeUsage is not null && !RealtimeUsage.IsOverLimit(_settings) &&
        !string.IsNullOrWhiteSpace(RealtimeApiKey?.Invoke());

    /// <summary>Clock used for rate limits (replaceable in tests).</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public CBRadioService(
        List<TruckerPersonality> roster,
        VirtualCompanyProfile company,
        IAudioPriorityManager audio,
        Func<AtsTelemetrySnapshot> telemetry,
        AiSettings settings,
        ILlmClient? llm = null,
        ISpeechToText? stt = null,
        ITextToSpeech? tts = null,
        Random? random = null)
    {
        _roster = roster;
        _company = company;
        _audio = audio;
        _telemetry = telemetry;
        _settings = settings;
        _llm = llm;
        _stt = stt;
        _tts = tts;
        _random = random ?? new Random();
    }

    public void SwitchChannel(int channelNumber)
    {
        if (channelNumber is < 1 or > 40) return;
        ActiveChannel = channelNumber;
    }

    /// <summary>Stops unprompted chatter that is still being prepared (used when a quiet period starts).</summary>
    public void CancelChatter()
    {
        lock (_stateLock)
        {
            _chatterCts.Cancel();
            _chatterCts = new CancellationTokenSource();
        }
    }

    public void BeginPushToTalk()
    {
        lock (_stateLock)
        {
            if (CurrentState == CBRadioState.RadioOff || IsPlayerExchange(CurrentState)) return; // previous transmission still in progress
            if (CurrentState is CBRadioState.PlayingAiResponse or CBRadioState.PlayingTransmission or CBRadioState.IncomingTransmission)
            {
                if (CollisionPolicy == HalfDuplexCollisionPolicy.IgnoreUntilClear) return;
                if (CollisionPolicy == HalfDuplexCollisionPolicy.InterruptSpeaker) _audio.StopAll();
            }
            // The player always wins over background chatter.
            _chatterCts.Cancel(); // not disposed: chatter may still be linking to it
            _chatterCts = new CancellationTokenSource();
            _txVersion++;
            _audio.IsRadioChannelBusy = true;
            SetState(CBRadioState.PlayerTransmitting);
        }
    }

    public async Task EndPushToTalkWithAudioAsync(byte[] wavAudio, CancellationToken cancellationToken = default)
    {
        if (CurrentState != CBRadioState.PlayerTransmitting) return;
        TransitionTo(CBRadioState.ProcessingSpeech);

        // Realtime speech-to-speech: the same session hears the player and answers in voice.
        if (RealtimeReady && ActiveChannel != EmergencyChannel)
        {
            RealtimeVoiceSession? rt = null;
            try
            {
                rt = await RealtimeVoiceSession.StartAsync(RealtimeTransportFactory!(), _settings, RealtimeUsage!, RealtimeApiKey!()!,
                    "You are a voice on a CB radio. Wait for your character instructions.", "ash", cancellationToken);
                string? heard = await rt.SendPlayerAudioAsync(wavAudio, cancellationToken);
                if (heard is not null)
                {
                    if (heard.Length == 0)
                    {
                        Notice?.Invoke(this, "Didn't catch that - nothing was heard.");
                        ReturnToIdle();
                        return;
                    }
                    await RespondAsync(heard, cancellationToken, rt);
                    return;
                }
                Notice?.Invoke(this, (rt.LastError ?? "Realtime voice unavailable.") + " Using the standard voice pipeline.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Notice?.Invoke(this, $"Realtime voice failed ({ex.Message}). Using the standard voice pipeline.");
            }
            finally
            {
                if (rt is not null) await rt.DisposeAsync();
            }
        }

        string? transcript = null;
        if (_stt?.IsAvailable == true)
            transcript = await _stt.TranscribeAsync(wavAudio, cancellationToken);
        else
            Notice?.Invoke(this, "Voice transcription is unavailable: enable AI (OpenAI) or offline recognition in AI settings, or type your message.");

        if (string.IsNullOrWhiteSpace(transcript))
        {
            if (_stt?.IsAvailable == true) Notice?.Invoke(this, "Didn't catch that - nothing was heard.");
            ReturnToIdle();
            return;
        }
        await RespondAsync(transcript, cancellationToken);
    }

    public async Task EndPushToTalkAsync(string? playerTranscript = null, CancellationToken cancellationToken = default)
    {
        if (CurrentState != CBRadioState.PlayerTransmitting) return;
        if (string.IsNullOrWhiteSpace(playerTranscript))
        {
            ReturnToIdle();
            return;
        }
        TransitionTo(CBRadioState.ProcessingSpeech);
        await RespondAsync(playerTranscript.Trim(), cancellationToken);
    }

    private async Task RespondAsync(string playerText, CancellationToken ct, RealtimeVoiceSession? rt = null)
    {
        int version;
        lock (_stateLock) version = _txVersion;
        // Wait briefly for cancelled chatter to let go of the radio.
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5), ct)) { ReturnToIdleIfCurrent(version); return; }
        try
        {
            var snapshot = _telemetry();
            int channel = ActiveChannel;
            string playerHandle = _company.DisplayHandle;

            // "TruckingAdvisor, ..." is an app command, not radio talk.
            if (VoiceCommandParser.TryParse(playerText) is { } command)
            {
                _audio.IsRadioChannelBusy = false;
                if (command.Kind == VoiceCommandKind.SwitchChannel && command.Channel is { } ch) SwitchChannel(ch);
                CommandReceived?.Invoke(this, command);
                return;
            }

            if (HandleDetector.TryDetect(playerText) is { } newHandle &&
                !string.Equals(newHandle, _company.PlayerHandle, StringComparison.OrdinalIgnoreCase))
                HandleChangeProposed?.Invoke(this, newHandle);

            Say(playerHandle, playerHandle, playerText, channel);
            _audio.IsRadioChannelBusy = false;
            TransitionTo(CBRadioState.GeneratingResponse);

            if (channel == DispatchChannel)
            {
                string prompt = ContextBuilder.BuildDispatcherSystemPrompt(_company, snapshot, RecentTurns());
                var (rtText, rtWav) = await RealtimeReplyAsync(rt, prompt, DispatchVoice, ct);
                string reply = rtText ?? await GenerateAsync(prompt, playerText, ct) ?? OfflineDispatchReply();
                if (rtWav is null) await PauseAsync(ct);
                await SpeakAsync(DispatchSpeaker, "Dispatch", reply, DispatchVoice, AudioPriority.Normal, channel, ct, preparedWav: rtWav);
            }
            else if (channel == EmergencyChannel)
            {
                await SpeakAsync("Channel 9", "Channel 9", "Channel nine is kept clear for emergencies, driver. Switch to one-nine for chatter.",
                    "onyx", AudioPriority.Normal, channel, ct);
            }
            else
            {
                var partner = PickPartner(playerText);
                ActivePartner = partner;
                string prompt = TruckerPrompt(partner, snapshot, channel);
                var (rtText, rtWav) = await RealtimeReplyAsync(rt, prompt, partner.VoiceId, ct);
                string reply = rtText ?? await GenerateAsync(prompt, playerText, ct, MaxTokensForMode())
                    ?? OfflineTruckerReply(partner, playerText);

                string where = string.IsNullOrEmpty(snapshot.DestinationCity) || !snapshot.JobActive
                    ? string.Empty : $" while hauling toward {snapshot.DestinationCity}";
                RelationshipManager.ApplyExchange(partner, playerText, reply, where, DateTimeOffset.UtcNow, MemoryLimit);
                TruckerUpdated?.Invoke(this, partner);
                if (rtWav is null) await PauseAsync(ct);
                await SpeakAsync(partner.FullName, partner.Handle, reply, partner.VoiceId, AudioPriority.Normal, channel, ct, preparedWav: rtWav);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Notice?.Invoke(this, $"Radio error: {ex.Message}");
        }
        finally
        {
            _gate.Release();
            ReturnToIdleIfCurrent(version);
        }
    }

    public async Task TriggerDynamicEncounterAsync(AtsTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (!ChatterAllowed() || CurrentState != CBRadioState.RadioIdle || _roster.Count == 0 || ActiveChannel is DispatchChannel or EmergencyChannel) return;
        if (!await _gate.WaitAsync(0, cancellationToken)) return;
        CancellationToken chatterToken;
        int version;
        lock (_stateLock) { chatterToken = _chatterCts.Token; version = _txVersion; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, chatterToken);
        cancellationToken = linked.Token;
        try
        {
            if (!TryConsumeUnsolicited()) return;
            var trucker = MaybeMeetNewTrucker() ?? PickRandomTrucker();
            string situation = DescribeEncounter(snapshot, trucker, ParkedFor, _random);
            if (!TryChatterState(CBRadioState.IncomingTransmission, version)) return;
            string line = await GenerateAsync(
                TruckerPrompt(trucker, snapshot, ActiveChannel),
                $"(No one has spoken to you. Make one unprompted CB call to the channel about: {situation})", cancellationToken, MaxTokensForMode())
                ?? OfflineEncounterLine(snapshot, trucker);
            ActivePartner = trucker;
            await SpeakAsync(trucker.FullName, trucker.Handle, line, trucker.VoiceId, AudioPriority.Low, ActiveChannel, cancellationToken, chatterVersion: version);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _gate.Release();
            ReturnToIdleIfCurrent(version);
        }
    }

    public async Task TriggerAiToAiBanterAsync(AtsTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (!ChatterAllowed() || CurrentState != CBRadioState.RadioIdle || _roster.Count < 2 || ActiveChannel is DispatchChannel or EmergencyChannel) return;
        if (!await _gate.WaitAsync(0, cancellationToken)) return;
        CancellationToken chatterToken;
        int version;
        lock (_stateLock) { chatterToken = _chatterCts.Token; version = _txVersion; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, chatterToken);
        cancellationToken = linked.Token;
        try
        {
            if (!TryConsumeUnsolicited()) return;
            TruckerPersonality a, b;
            lock (_roster)
            {
                int i = _random.Next(_roster.Count);
                int j = (i + 1 + _random.Next(_roster.Count - 1)) % _roster.Count; // always different from i
                a = _roster[i];
                b = _roster[j];
            }
            if (!TryChatterState(CBRadioState.PlayingTransmission, version)) return;
            // One API call for the whole exchange keeps cost down.
            var lines = await GenerateBanterAsync(a, b, snapshot, cancellationToken) ?? OfflineBanter(a, b);
            RememberBanter(a, b, lines);
            foreach (var (handle, text) in lines)
            {
                var speaker = handle.Equals(b.Handle, StringComparison.OrdinalIgnoreCase) ? b : a;
                await SpeakAsync(speaker.FullName, speaker.Handle, text, speaker.VoiceId, AudioPriority.Low, ActiveChannel, cancellationToken, chatterVersion: version);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _gate.Release();
            ReturnToIdleIfCurrent(version);
        }
    }

    private async Task<List<(string Handle, string Text)>?> GenerateBanterAsync(TruckerPersonality a, TruckerPersonality b, AtsTelemetrySnapshot snapshot, CancellationToken ct)
    {
        if (_llm?.IsAvailable != true) return null;
        int lineCount = 4 + _random.Next(3);
        var result = await _llm.CompleteAsync(
            "You write short, natural CB radio dialogue. Output JSON only.",
            ContextBuilder.BuildBanterPrompt(a, b, _company, snapshot, lineCount),
            _settings.MaxResponseTokens * 3, ct);
        return result is null ? null : ParseBanter(result.Text, a, b);
    }

    /// <summary>Parses {"lines":[{"handle","text"}]}; tolerates code fences. Null if unusable.</summary>
    public static List<(string Handle, string Text)>? ParseBanter(string raw, TruckerPersonality a, TruckerPersonality b)
    {
        string json = raw.Trim();
        int start = json.IndexOf('{');
        int end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(json[start..(end + 1)]);
            var lines = new List<(string, string)>();
            foreach (var el in doc.RootElement.GetProperty("lines").EnumerateArray())
            {
                string handle = el.GetProperty("handle").GetString() ?? "";
                string text = el.GetProperty("text").GetString()?.Trim() ?? "";
                if (text.Length == 0) continue;
                if (!handle.Equals(a.Handle, StringComparison.OrdinalIgnoreCase) && !handle.Equals(b.Handle, StringComparison.OrdinalIgnoreCase))
                    handle = lines.Count % 2 == 0 ? a.Handle : b.Handle;
                lines.Add((handle, text));
                if (lines.Count == 8) break;
            }
            return lines.Count >= 2 ? lines : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Dispatch calls the driver on its own (briefing, check-in, reminder). Delivered on the company
    /// channel whatever CB channel is selected. Yields to the player and to an exchange in progress;
    /// returns false if it couldn't go out now so the caller can retry later.
    /// </summary>
    public async Task<bool> SendDispatchMessageAsync(string facts, bool important, CancellationToken cancellationToken = default)
    {
        if (CurrentState is not (CBRadioState.RadioIdle or CBRadioState.AwaitingPlayerResponse)) return false;
        if (!await _gate.WaitAsync(0, cancellationToken)) return false;
        CancellationToken chatterToken;
        int version;
        lock (_stateLock) { chatterToken = _chatterCts.Token; version = _txVersion; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, chatterToken);
        var ct = linked.Token;
        try
        {
            if (!important && IsQuietPeriod) return true; // the driver asked for quiet: drop routine calls
            if (!important && !TryConsumeUnsolicited()) return true; // over the hourly cap: drop quietly
            if (!TryChatterState(CBRadioState.IncomingTransmission, version)) return false;
            string line = await GenerateAsync(
                ContextBuilder.BuildDispatcherSystemPrompt(_company, _telemetry(), RecentTurns()),
                $"(Make one short call to your driver, starting by hailing them, to tell them: {facts})", ct)
                ?? $"{DispatchSpeaker} to {_company.DisplayHandle}. {facts}";
            await SpeakAsync(DispatchSpeaker, "Dispatch", line, DispatchVoice, important ? AudioPriority.High : AudioPriority.Normal,
                DispatchChannel, ct, chatterVersion: version);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
            ReturnToIdleIfCurrent(version);
        }
    }

    private const string DispatchVoice = "coral";
    private string DispatchSpeaker => $"{_company.DispatcherName} (Dispatch)";

    private string TruckerPrompt(TruckerPersonality trucker, AtsTelemetrySnapshot snapshot, int channel) =>
        ContextBuilder.BuildTruckerSystemPrompt(trucker, _company, snapshot, RecentTurns(), channel, _settings.ConversationMode, Roster);

    private int MemoryLimit => _settings.RememberConversations ? _settings.MaxMemoriesPerTrucker : 0;

    private int MaxTokensForMode() => _settings.ConversationMode switch
    {
        ConversationTimingMode.Brief => Math.Min(80, _settings.MaxResponseTokens),
        ConversationTimingMode.Chatty => _settings.MaxResponseTokens * 2,
        _ => _settings.MaxResponseTokens
    };

    /// <summary>
    /// "Quiet for a while": until this time nobody calls you unprompted (no chatter, no routine dispatch
    /// check-ins). Your own calls, replies to you and important dispatch notices still work. Null = off.
    /// </summary>
    public DateTimeOffset? QuietUntil { get; set; }

    /// <summary>True while a temporary quiet period is running.</summary>
    public bool IsQuietPeriod => QuietUntil is { } until && Clock() < until;

    /// <summary>Quiet mode, activity "Off" and a quiet period mean truckers only talk when you call them.</summary>
    private bool ChatterAllowed() =>
        _settings.ConversationMode != ConversationTimingMode.Quiet && _settings.RadioActivity != RadioActivitySetting.Off && !IsQuietPeriod;

    /// <summary>Hourly cap on calls nobody asked for. Returns false when the cap is reached.</summary>
    private bool TryConsumeUnsolicited()
    {
        var now = Clock();
        lock (_unsolicited)
        {
            while (_unsolicited.Count > 0 && now - _unsolicited.Peek() > TimeSpan.FromHours(1)) _unsolicited.Dequeue();
            if (_settings.MaxUnsolicitedPerHour > 0 && _unsolicited.Count >= _settings.MaxUnsolicitedPerHour) return false;
            _unsolicited.Enqueue(now);
            return true;
        }
    }

    private Task PauseAsync(CancellationToken ct)
    {
        var (min, max) = ReplyPause;
        int ms = max > min ? _random.Next(min, max) : min;
        return ms > 0 ? Task.Delay(ms, ct) : Task.CompletedTask;
    }

    /// <summary>Overheard exchanges become shared radio-world memories for both truckers.</summary>
    private void RememberBanter(TruckerPersonality a, TruckerPersonality b, List<(string Handle, string Text)> lines)
    {
        if (!_settings.RememberConversations || lines.Count == 0) return;
        string gist = lines[0].Text.Length > 80 ? lines[0].Text[..80].TrimEnd() + "..." : lines[0].Text;
        var now = DateTimeOffset.UtcNow;
        foreach (var (me, other) in new[] { (a, b), (b, a) })
        {
            lock (me.Sync)
            {
                me.SummarizedMemories.Add($"{now:MMM d}: chatted on the radio with {other.Handle} (\"{gist.Replace('"', '\'')}\")");
                if (me.SummarizedMemories.Count > _settings.MaxMemoriesPerTrucker)
                    me.SummarizedMemories.RemoveRange(0, me.SummarizedMemories.Count - _settings.MaxMemoriesPerTrucker);
            }
            TruckerUpdated?.Invoke(this, me);
        }
    }

    private async Task<string?> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct, int? maxTokens = null)
    {
        if (_llm?.IsAvailable != true) return null;
        var result = await _llm.CompleteAsync(systemPrompt, userPrompt, maxTokens ?? _settings.MaxResponseTokens, ct);
        return result?.Text;
    }

    /// <summary>Realtime voice reply in the given character, or (null, null) to fall back to the modular pipeline.</summary>
    private async Task<(string? Text, byte[]? Wav)> RealtimeReplyAsync(RealtimeVoiceSession? rt, string instructions, string voice, CancellationToken ct)
    {
        if (rt is null) return (null, null);
        try
        {
            await rt.UpdateInstructionsAsync(instructions, voice, ct);
            var reply = await rt.ReplyAsync(MaxTokensForMode(), ct);
            if (reply is { Text.Length: > 0 }) return (reply.Text, reply.WavAudio);
            Notice?.Invoke(this, (rt.LastError ?? "Realtime reply was empty.") + " Using the standard voice pipeline.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice?.Invoke(this, $"Realtime voice failed ({ex.Message}). Using the standard voice pipeline.");
        }
        return (null, null);
    }

    private async Task SpeakAsync(string speaker, string handle, string text, string voiceId, AudioPriority priority, int channel, CancellationToken ct, int? chatterVersion = null, byte[]? preparedWav = null)
    {
        ct.ThrowIfCancellationRequested();
        byte[]? wav = preparedWav;
        if (wav is null && _tts?.IsAvailable == true)
            wav = await _tts.SynthesizeAsync(text, voiceId, ct);
        if (chatterVersion is { } v)
        {
            // Chatter never takes the radio from the player.
            if (ct.IsCancellationRequested || !TryChatterState(CBRadioState.PlayingAiResponse, v)) return;
        }
        else
        {
            TransitionTo(CBRadioState.PlayingAiResponse);
        }
        Say(speaker, handle, text, channel);
        // Wait until the voice has actually finished, so the radio isn't "idle" while someone is talking.
        // Without OpenAI audio, the sink speaks Text with a Windows voice.
        await _audio.EnqueueAndWaitAsync(
            new AudioItem(text, priority, channel == DispatchChannel ? "dispatcher" : "cb", ApplyCbEffects: true, WavAudio: wav, VoiceId: voiceId,
                NeuralVoice: NeuralVoiceFor(handle)), ct);
    }

    /// <summary>Each speaker keeps their own free neural voice.</summary>
    private string NeuralVoiceFor(string handle)
    {
        if (handle == "Dispatch") return DispatchNeuralVoice;
        if (handle == "Channel 9") return "en-US-GuyNeural|-6%|-4Hz";
        TruckerPersonality? t;
        lock (_roster) t = _roster.FirstOrDefault(r => r.Handle == handle);
        return t is null ? PersonalityCatalog.NeuralVoicePool[0] : PersonalityCatalog.NeuralVoiceFor(t);
    }

    private const string DispatchNeuralVoice = "en-US-AriaNeural|+4%|+0Hz";

    private RadioTurn[] RecentTurns()
    {
        lock (_recent) return _recent.ToArray();
    }

    private void Say(string speaker, string handle, string text, int channel)
    {
        lock (_recent)
        {
            _recent.Add(new RadioTurn(handle, text, channel));
            if (_recent.Count > Math.Max(2, _settings.MaxConversationTurns * 2)) _recent.RemoveAt(0);
        }
        TransmissionReceived?.Invoke(this, (speaker, handle, text, channel));
    }

    private TruckerPersonality PickPartner(string playerText)
    {
        lock (_roster)
        {
            var named = _roster.FirstOrDefault(t => playerText.Contains(t.Handle, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return named;
            if (ActivePartner is { LastSpokenAt: { } last } current && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(10))
                return current;
            return _roster[_random.Next(_roster.Count)];
        }
    }

    private TruckerPersonality PickRandomTrucker()
    {
        lock (_roster) return _roster[_random.Next(_roster.Count)];
    }

    /// <summary>Occasionally a new (generated) trucker shows up; they persist like the core cast.</summary>
    private TruckerPersonality? MaybeMeetNewTrucker()
    {
        const int coreCast = 5, maxExtra = 4;
        TruckerPersonality newcomer;
        lock (_roster)
        {
            int extra = _roster.Count - coreCast;
            if (extra >= maxExtra || _random.NextDouble() >= 0.15) return null;
            newcomer = PersonalityCatalog.GenerateDynamicTrucker(Math.Max(0, extra));
            if (_roster.Any(t => t.Id == newcomer.Id)) return null;
            _roster.Add(newcomer);
        }
        TruckerUpdated?.Invoke(this, newcomer);
        return newcomer;
    }

    private static readonly string[] TruckStopTopics =
    {
        "asking if anyone else is shutting down for the night at a truck stop",
        "looking for a decent place to grab breakfast nearby",
        "what a long haul today has been",
        "favorite trucks or truck customization",
        "a memorable delivery from years back",
        "favorite highways and scenery",
        "fuel economy tricks"
    };

    private static string DescribeEncounter(AtsTelemetrySnapshot s, TruckerPersonality t, TimeSpan parkedFor, Random random)
    {
        if (parkedFor >= TimeSpan.FromMinutes(5))
            return TruckStopTopics[random.Next(TruckStopTopics.Length)] + " (relaxed truck-stop talk; you're parked too)";
        if (s.HasFuelCapacity && s.FuelPercent < 25 && t.Relationship >= RelationshipLevel.RoadAcquaintance)
            return "checking whether the player plans a fuel stop soon (their fuel is low)";
        if (s.JobActive && s.CargoWeightLbs >= 38000)
            return "the heavy load the player is pulling and where they're headed";
        if (s.GameTimeMinutes > 0 && s.IsNightDriving)
            return "asking who else is driving late tonight";
        return "general road chatter or a short trucking story";
    }

    // ---- Offline lines (AI disabled or unavailable) ----

    private string OfflineTruckerReply(TruckerPersonality partner, string playerText)
    {
        string lower = playerText.ToLowerInvariant();
        string handle = _company.DisplayHandle;
        if (lower.Contains("clear") || lower.Contains("stay safe") || lower.Contains("later"))
            return $"Ten-four, {handle}. Keep the shiny side up. {partner.Handle} clear.";
        if (lower.Contains("breaker") || lower.Contains("copy") || lower.Contains("anybody"))
            return $"Yeah, I copy you, {handle}. This is {partner.Handle} in the {partner.Truck}.";
        return $"Ten-four, {handle}. {partner.Handle} standing by.";
    }

    private string OfflineDispatchReply() => $"Dispatch copies, {_company.DisplayHandle}. Keep us posted.";

    private string OfflineEncounterLine(AtsTelemetrySnapshot s, TruckerPersonality t) =>
        s.GameTimeMinutes > 0 && s.IsNightDriving
            ? $"Breaker one-nine, {t.Handle} here. Anybody else burning the midnight diesel?"
            : $"Breaker one-nine, {t.Handle} rolling. Hope everybody's having a clean run.";

    private static List<(string, string)> OfflineBanter(TruckerPersonality a, TruckerPersonality b) => new()
    {
        (a.Handle, $"Breaker one-nine, {b.Handle}, you on here?"),
        (b.Handle, $"Yeah, {a.Handle}, I got you. Rolling along fine on my end."),
        (a.Handle, "Ten-four. Just checking in. Holler if you need anything."),
        (b.Handle, "Will do. Keep it between the lines.")
    };

    private static bool IsPlayerExchange(CBRadioState s) =>
        s is CBRadioState.PlayerTransmitting or CBRadioState.ProcessingSpeech or CBRadioState.GeneratingResponse;

    /// <summary>Background chatter may only change state if the player hasn't keyed up since it started.</summary>
    private bool TryChatterState(CBRadioState next, int version)
    {
        lock (_stateLock)
        {
            if (version != _txVersion || IsPlayerExchange(CurrentState)) return false;
            SetState(next);
            return true;
        }
    }

    /// <summary>Goes idle only if no newer transmission has started since <paramref name="version"/>.</summary>
    private void ReturnToIdleIfCurrent(int version)
    {
        lock (_stateLock)
        {
            if (version != _txVersion) return;
            _audio.IsRadioChannelBusy = false;
            SetState(CBRadioState.RadioIdle);
        }
    }

    private void ReturnToIdle()
    {
        lock (_stateLock)
        {
            _audio.IsRadioChannelBusy = false;
            SetState(CBRadioState.RadioIdle);
        }
    }

    private void TransitionTo(CBRadioState next)
    {
        lock (_stateLock) SetState(next);
    }

    private void SetState(CBRadioState next)
    {
        if (CurrentState == next) return;
        CurrentState = next;
        StateChanged?.Invoke(this, next);
    }
}
