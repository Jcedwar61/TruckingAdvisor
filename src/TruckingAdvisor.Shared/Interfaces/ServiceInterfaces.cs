using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Shared.Interfaces;

public interface ITelemetryProvider : IDisposable
{
    /// <summary>Raised on a background thread.</summary>
    event EventHandler<AtsTelemetrySnapshot>? TelemetryUpdated;
    event EventHandler<ConnectionState>? ConnectionStateChanged;
    ConnectionState CurrentState { get; }
    AtsTelemetrySnapshot LatestSnapshot { get; }
    void Start();
    void Stop();
}

public interface IDrivingAnalyzer
{
    event EventHandler<DrivingIncident>? IncidentDetected;
    void EvaluateSnapshot(AtsTelemetrySnapshot current, AtsTelemetrySnapshot? previous);
}

/// <summary>One thing to say or play. Exactly one of Text or WavAudio is used.</summary>
public sealed record AudioItem(
    string Text,
    AudioPriority Priority,
    string Channel = "advisor",
    bool ApplyCbEffects = false,
    byte[]? WavAudio = null,
    string? VoiceId = null);

/// <summary>Plays one item and completes when playback really ends (or is cancelled).</summary>
public interface IAudioSink
{
    Task PlayAsync(AudioItem item, float volume0To1, CancellationToken cancellationToken);
}

public interface IAudioPriorityManager
{
    void EnqueueAnnouncement(string text, AudioPriority priority, string voiceProfile = "advisor", bool applyCbEffects = false);
    void Enqueue(AudioItem item);
    /// <summary>Queues the item and completes when it has finished playing, was dropped, or was stopped.</summary>
    Task EnqueueAndWaitAsync(AudioItem item, CancellationToken cancellationToken = default);
    void SetChannelVolume(string channel, float volume0To1);
    void StopAll();
    /// <summary>True while the CB radio is receiving or transmitting; low-priority items are dropped.</summary>
    bool IsRadioChannelBusy { get; set; }
}

/// <summary>Token usage reported by the AI provider for one call.</summary>
public sealed record LlmUsage(int InputTokens, int OutputTokens);

public sealed record LlmResult(string Text, LlmUsage Usage);

/// <summary>Chat model abstraction so the radio does not depend on one provider or model.</summary>
public interface ILlmClient
{
    /// <summary>False when AI is disabled, no key is stored, or the spending limit is reached.</summary>
    bool IsAvailable { get; }
    Task<LlmResult?> CompleteAsync(string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken cancellationToken);
}

public interface ISpeechToText
{
    bool IsAvailable { get; }
    /// <summary>Transcribes a WAV recording. Returns null on failure.</summary>
    Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken cancellationToken);
}

public interface ITextToSpeech
{
    bool IsAvailable { get; }
    /// <summary>Returns WAV audio, or null on failure (caller falls back to Windows voices).</summary>
    Task<byte[]?> SynthesizeAsync(string text, string voiceId, CancellationToken cancellationToken);
}

public interface ICBRadioService
{
    event EventHandler<CBRadioState>? StateChanged;
    event EventHandler<(string Speaker, string Handle, string Transcript, int Channel)>? TransmissionReceived;
    CBRadioState CurrentState { get; }
    int ActiveChannel { get; }
    void SwitchChannel(int channelNumber);
    void BeginPushToTalk();
    /// <summary>Player released PTT. <paramref name="playerTranscript"/> is what they said (typed or transcribed).</summary>
    Task EndPushToTalkAsync(string? playerTranscript = null, CancellationToken cancellationToken = default);
    /// <summary>Player released PTT with a mic recording; transcribes it, then answers.</summary>
    Task EndPushToTalkWithAudioAsync(byte[] wavAudio, CancellationToken cancellationToken = default);
    Task TriggerDynamicEncounterAsync(AtsTelemetrySnapshot snapshot, CancellationToken cancellationToken = default);
    Task TriggerAiToAiBanterAsync(AtsTelemetrySnapshot snapshot, CancellationToken cancellationToken = default);
}
