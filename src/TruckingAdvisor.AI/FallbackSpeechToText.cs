using TruckingAdvisor.Shared.Interfaces;

namespace TruckingAdvisor.AI;

/// <summary>Uses the primary recognizer (OpenAI) when available, otherwise the local one (Windows).</summary>
public sealed class FallbackSpeechToText : ISpeechToText
{
    private readonly ISpeechToText _primary;
    private readonly ISpeechToText? _fallback;

    public FallbackSpeechToText(ISpeechToText primary, ISpeechToText? fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    /// <summary>Which recognizer handled the last clip ("OpenAI", "Windows (offline)", or empty).</summary>
    public string LastUsed { get; private set; } = string.Empty;

    public bool IsAvailable => _primary.IsAvailable || _fallback?.IsAvailable == true;

    public async Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken cancellationToken)
    {
        if (_primary.IsAvailable)
        {
            var text = await _primary.TranscribeAsync(wavAudio, cancellationToken);
            if (!string.IsNullOrWhiteSpace(text)) { LastUsed = "OpenAI"; return text; }
        }
        if (_fallback?.IsAvailable == true)
        {
            var text = await _fallback.TranscribeAsync(wavAudio, cancellationToken);
            if (!string.IsNullOrWhiteSpace(text)) { LastUsed = "Windows (offline)"; return text; }
        }
        LastUsed = string.Empty;
        return null;
    }
}
