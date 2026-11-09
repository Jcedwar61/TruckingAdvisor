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

    public event EventHandler<CBRadioState>? StateChanged;
    public event EventHandler<(string Speaker, string Handle, string Transcript, int Channel)>? TransmissionReceived;
    /// <summary>Raised after a trucker's relationship/memory changed, so it can be saved.</summary>
    public event EventHandler<TruckerPersonality>? TruckerUpdated;
    /// <summary>Short status for the UI ("AI offline", "No speech heard", ...).</summary>
    public event EventHandler<string>? Notice;

    public CBRadioState CurrentState { get; private set; } = CBRadioState.RadioIdle;
    public int ActiveChannel { get; private set; } = 19;
    public HalfDuplexCollisionPolicy CollisionPolicy { get; set; } = HalfDuplexCollisionPolicy.InterruptSpeaker;
    public TruckerPersonality? ActivePartner { get; private set; }
    public IReadOnlyList<TruckerPersonality> Roster { get { lock (_roster) return _roster.ToArray(); } }
    public bool AiAvailable => _llm?.IsAvailable == true;

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

        string? transcript = null;
        if (_stt?.IsAvailable == true)
            transcript = await _stt.TranscribeAsync(wavAudio, cancellationToken);
        else
            Notice?.Invoke(this, "Voice transcription is off (enable AI and the microphone in AI settings). Type your message instead.");

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

    private async Task RespondAsync(string playerText, CancellationToken ct)
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
            Say(playerHandle, playerHandle, playerText, channel);
            _audio.IsRadioChannelBusy = false;
            TransitionTo(CBRadioState.GeneratingResponse);

            if (channel == DispatchChannel)
            {
                string reply = await GenerateAsync(
                    ContextBuilder.BuildDispatcherSystemPrompt(_company, snapshot, RecentTurns()), playerText, ct)
                    ?? OfflineDispatchReply();
                await SpeakAsync("Dispatch", "Dispatch", reply, "coral", AudioPriority.Normal, channel, ct);
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
                string reply = await GenerateAsync(
                    ContextBuilder.BuildTruckerSystemPrompt(partner, _company, snapshot, RecentTurns(), channel), playerText, ct)
                    ?? OfflineTruckerReply(partner, playerText);

                string where = string.IsNullOrEmpty(snapshot.DestinationCity) || !snapshot.JobActive
                    ? string.Empty : $" while hauling toward {snapshot.DestinationCity}";
                RelationshipManager.ApplyExchange(partner, playerText, reply, where, DateTimeOffset.UtcNow, _settings.MaxMemoriesPerTrucker);
                TruckerUpdated?.Invoke(this, partner);
                await SpeakAsync(partner.FullName, partner.Handle, reply, partner.VoiceId, AudioPriority.Normal, channel, ct);
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
        if (CurrentState != CBRadioState.RadioIdle || _roster.Count == 0 || ActiveChannel is DispatchChannel or EmergencyChannel) return;
        if (!await _gate.WaitAsync(0, cancellationToken)) return;
        CancellationToken chatterToken;
        int version;
        lock (_stateLock) { chatterToken = _chatterCts.Token; version = _txVersion; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, chatterToken);
        cancellationToken = linked.Token;
        try
        {
            var trucker = MaybeMeetNewTrucker() ?? PickRandomTrucker();
            string situation = DescribeEncounter(snapshot, trucker);
            if (!TryChatterState(CBRadioState.IncomingTransmission, version)) return;
            string line = await GenerateAsync(
                ContextBuilder.BuildTruckerSystemPrompt(trucker, _company, snapshot, RecentTurns(), ActiveChannel),
                $"(No one has spoken to you. Make one unprompted CB call to the channel about: {situation})", cancellationToken)
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
        if (CurrentState != CBRadioState.RadioIdle || _roster.Count < 2 || ActiveChannel is DispatchChannel or EmergencyChannel) return;
        if (!await _gate.WaitAsync(0, cancellationToken)) return;
        CancellationToken chatterToken;
        int version;
        lock (_stateLock) { chatterToken = _chatterCts.Token; version = _txVersion; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, chatterToken);
        cancellationToken = linked.Token;
        try
        {
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

    private async Task<string?> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        if (_llm?.IsAvailable != true) return null;
        var result = await _llm.CompleteAsync(systemPrompt, userPrompt, _settings.MaxResponseTokens, ct);
        return result?.Text;
    }

    private async Task SpeakAsync(string speaker, string handle, string text, string voiceId, AudioPriority priority, int channel, CancellationToken ct, int? chatterVersion = null)
    {
        ct.ThrowIfCancellationRequested();
        byte[]? wav = null;
        if (_tts?.IsAvailable == true)
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
            new AudioItem(text, priority, channel == DispatchChannel ? "dispatcher" : "cb", ApplyCbEffects: true, WavAudio: wav, VoiceId: voiceId), ct);
    }

    private RadioTurn[] RecentTurns()
    {
        lock (_recent) return _recent.ToArray();
    }

    private void Say(string speaker, string handle, string text, int channel)
    {
        lock (_recent)
        {
            _recent.Add(new RadioTurn(handle, text));
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

    private static string DescribeEncounter(AtsTelemetrySnapshot s, TruckerPersonality t)
    {
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
