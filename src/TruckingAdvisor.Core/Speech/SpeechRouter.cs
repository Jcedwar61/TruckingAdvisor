using TruckingAdvisor.Shared.Interfaces;

namespace TruckingAdvisor.Core.Speech;

/// <summary>Which recognizer to use for push-to-talk.</summary>
public enum SpeechRecognition
{
    /// <summary>OpenAI when AI is on, else Whisper on this PC, else Windows.</summary>
    Auto = 0,
    /// <summary>Whisper on this PC (free, private, offline).</summary>
    OnThisPc = 1,
    /// <summary>OpenAI first (needs AI on); Whisper on this PC if it fails.</summary>
    OpenAI = 2,
    /// <summary>Windows dictation only (least accurate).</summary>
    WindowsBasic = 3
}

/// <summary>
/// Cleans each push-to-talk clip once (silence trimmed, quiet mics boosted, near-silence rejected),
/// then tries recognizers in order until one hears something.
/// </summary>
public sealed class SpeechRouter : ISpeechToText
{
    private readonly Func<IReadOnlyList<(string Name, ISpeechToText Engine)>> _order;

    public SpeechRouter(Func<IReadOnlyList<(string Name, ISpeechToText Engine)>> order) => _order = order;

    /// <summary>Which recognizer handled the last clip, or why nothing was heard.</summary>
    public string LastUsed { get; private set; } = string.Empty;
    public SpeechClipInfo? LastClip { get; private set; }
    /// <summary>The last clip was rejected as silence (or too quiet) before recognition.</summary>
    public bool LastWasSilent { get; private set; }

    public bool IsAvailable => _order().Any(e => e.Engine.IsAvailable);

    public async Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken cancellationToken)
    {
        LastWasSilent = false;
        byte[] clean = wavAudio;
        try
        {
            var (raw, rate) = SpeechAudio.ReadWav(wavAudio);
            var (samples, info) = SpeechAudio.Prepare(raw, rate);
            LastClip = info;
            if (info.LooksSilent)
            {
                LastWasSilent = true;
                LastUsed = string.Empty;
                return null;
            }
            clean = SpeechAudio.ToWav(samples);
        }
        catch (InvalidDataException) { /* not a WAV we understand: pass it on unchanged */ }

        foreach (var (name, engine) in _order())
        {
            if (!engine.IsAvailable) continue;
            string? text;
            try { text = await engine.TranscribeAsync(clean, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                TruckingAdvisor.Shared.Diagnostics.AppLog.Error($"{name} speech recognition failed", ex);
                continue;
            }
            text = SpeechText.Clean(text);
            if (text is not null) { LastUsed = name; return text; }
        }
        LastUsed = string.Empty;
        return null;
    }
}
