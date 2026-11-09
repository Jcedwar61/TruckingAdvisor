using System.Globalization;
using System.Runtime.Versioning;
using System.Speech.Recognition;
using TruckingAdvisor.Shared.Interfaces;

namespace TruckingAdvisor.Audio;

/// <summary>
/// Offline speech recognition with the recognizer built into Windows (System.Speech, free dictation).
/// Less accurate than OpenAI transcription, but needs no internet or API key. Used as the fallback.
/// Requires an English speech recognizer installed in Windows (Settings > Time &amp; language > Speech).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSpeechToText : ISpeechToText
{
    private static readonly Lazy<RecognizerInfo?> Recognizer = new(() =>
    {
        try
        {
            var all = SpeechRecognitionEngine.InstalledRecognizers();
            return all.FirstOrDefault(r => r.Culture.Name.Equals(CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase) && r.Culture.TwoLetterISOLanguageName == "en")
                ?? all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en");
        }
        catch { return null; }
    });

    public bool Enabled { get; set; } = true;
    public bool IsAvailable => Enabled && Recognizer.Value is not null;

    public Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken cancellationToken) =>
        Task.Run<string?>(() =>
        {
            if (!IsAvailable || wavAudio.Length < 64) return null;
            using var engine = new SpeechRecognitionEngine(Recognizer.Value!);
            engine.LoadGrammar(new DictationGrammar());
            using var stream = new MemoryStream(wavAudio);
            engine.SetInputToWaveStream(stream);
            var words = new List<string>();
            // Read every phrase in the clip (a PTT message can contain pauses).
            while (!cancellationToken.IsCancellationRequested)
            {
                RecognitionResult? r;
                try { r = engine.Recognize(); }
                catch (InvalidOperationException) { break; } // end of stream
                if (r is null) break;
                if (r.Confidence >= 0.2f) words.Add(r.Text);
            }
            string text = string.Join(' ', words).Trim();
            return text.Length == 0 ? null : text;
        }, cancellationToken);
}
