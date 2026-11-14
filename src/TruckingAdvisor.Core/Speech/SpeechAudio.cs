using System.Buffers.Binary;
using System.Text;

namespace TruckingAdvisor.Core.Speech;

/// <summary>What clean-up found in a push-to-talk clip.</summary>
public sealed record SpeechClipInfo(double SpeechSeconds, float Peak, float Rms, float Gain, bool LooksSilent);

/// <summary>
/// Cleans a push-to-talk recording before recognition: mono 16 kHz, DC offset removed, silence trimmed
/// (with a little margin), quiet microphones boosted. Near-silent clips are flagged so they're not sent
/// (recognizers "hear" phrases like "Thank you." in silence).
/// </summary>
public static class SpeechAudio
{
    public const int Rate = 16000;

    /// <summary>Reads a PCM16 or IEEE-float WAV (any rate, mono or stereo) into mono floats.</summary>
    public static (float[] Samples, int Rate) ReadWav(byte[] wav)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            throw new InvalidDataException("Not a WAV file.");
        int pos = 12, channels = 1, rate = Rate, bits = 16, format = 1;
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(pos + 4));
            int body = pos + 8;
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14));
            }
            else if (id == "data")
            {
                // Recorders that never finished the header report 0 or -1: use what's actually there.
                int len = size <= 0 || body + size > wav.Length ? wav.Length - body : size;
                int bytesPer = bits / 8, frame = bytesPer * Math.Max(1, channels);
                int frames = len / frame;
                var mono = new float[frames];
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int o = body + f * frame + c * bytesPer;
                        sum += format == 3 && bits == 32 ? BinaryPrimitives.ReadSingleLittleEndian(wav.AsSpan(o))
                             : bits == 16 ? BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(o)) / 32768f
                             : bits == 8 ? (wav[o] - 128) / 128f
                             : 0;
                    }
                    mono[f] = sum / Math.Max(1, channels);
                }
                return (mono, rate);
            }
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException("WAV has no audio data.");
    }

    /// <summary>Linear resampling (speech only needs 16 kHz).</summary>
    public static float[] Resample(float[] input, int fromRate, int toRate = Rate)
    {
        if (fromRate == toRate || input.Length == 0) return input;
        int n = (int)((long)input.Length * toRate / fromRate);
        var output = new float[n];
        double step = (double)fromRate / toRate;
        for (int i = 0; i < n; i++)
        {
            double src = i * step;
            int j = (int)src;
            double t = src - j;
            float a = input[Math.Min(j, input.Length - 1)], b = input[Math.Min(j + 1, input.Length - 1)];
            output[i] = (float)(a + (b - a) * t);
        }
        return output;
    }

    /// <summary>Cleans a clip for recognition. Returns 16 kHz mono samples and what was found.</summary>
    public static (float[] Samples, SpeechClipInfo Info) Prepare(float[] samples, int rate)
    {
        var s = Resample(samples, rate);
        if (s.Length == 0) return (s, new SpeechClipInfo(0, 0, 0, 1, true));

        // DC offset (cheap mics and some USB interfaces).
        double mean = 0;
        foreach (var v in s) mean += v;
        mean /= s.Length;
        for (int i = 0; i < s.Length; i++) s[i] -= (float)mean;

        // Short-window energy to find where speech starts and ends.
        const int win = Rate / 50; // 20 ms
        int windows = s.Length / win;
        var energy = new float[Math.Max(1, windows)];
        float maxE = 0;
        for (int w = 0; w < windows; w++)
        {
            double e = 0;
            for (int i = w * win; i < (w + 1) * win; i++) e += s[i] * s[i];
            energy[w] = (float)Math.Sqrt(e / win);
            maxE = Math.Max(maxE, energy[w]);
        }
        float peak = s.Max(MathF.Abs);
        double rmsAll = Math.Sqrt(s.Select(v => (double)v * v).Average());

        // Noise floor = quiet 20th percentile; speech = clearly above it.
        var sorted = energy.OrderBy(e => e).ToArray();
        float floor = sorted[(int)(sorted.Length * 0.2)];
        float threshold = Math.Max(floor * 3f, Math.Max(0.004f, maxE * 0.08f));
        int first = Array.FindIndex(energy, e => e > threshold), last = Array.FindLastIndex(energy, e => e > threshold);
        int voiced = energy.Count(e => e > threshold);
        bool silent = first < 0 || voiced * 0.02 < 0.25 || maxE < 0.006f;
        if (silent) return (s, new SpeechClipInfo(voiced * 0.02, peak, (float)rmsAll, 1, true));

        // Keep 200 ms before and 300 ms after speech so word edges aren't clipped.
        int start = Math.Max(0, (first - 10) * win), end = Math.Min(s.Length, (last + 16) * win);
        var trimmed = s.AsSpan(start, end - start).ToArray();

        // Bring quiet microphones up to about -3 dBFS (at most 20x so noise isn't blown up).
        float tPeak = trimmed.Max(MathF.Abs);
        float gain = tPeak > 0 ? Math.Clamp(0.7f / tPeak, 1f, 20f) : 1f;
        if (gain > 1.01f) for (int i = 0; i < trimmed.Length; i++) trimmed[i] = Math.Clamp(trimmed[i] * gain, -1f, 1f);
        return (trimmed, new SpeechClipInfo(voiced * 0.02, peak, (float)rmsAll, gain, false));
    }

    /// <summary>16 kHz mono PCM16 WAV (what both OpenAI and Whisper accept).</summary>
    public static byte[] ToWav(float[] samples, int rate = Rate)
    {
        var ms = new MemoryStream(44 + samples.Length * 2);
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples.Length * 2); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples.Length * 2);
        foreach (var v in samples) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>Words that help recognizers spell names right, and the known hallucinations to drop.</summary>
public static class SpeechText
{
    public const string CbTerms = "CB radio, breaker one-nine, ten-four, copy that, come back, good buddy, handle, rig, " +
                                  "hammer down, bear, smokey, Jake brake, reefer, flatbed, dispatch, channel nineteen, TruckingAdvisor";

    /// <summary>A short recognition prompt: CB slang plus names the player is likely to say.</summary>
    public static string BuildPrompt(IEnumerable<string> names)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(25);
        string joined = string.Join(", ", list);
        return joined.Length == 0 ? CbTerms + "." : $"{CbTerms}. Names: {joined}.";
    }

    private static readonly HashSet<string> Hallucinations = new(StringComparer.OrdinalIgnoreCase)
    {
        "thank you", "thank you.", "thanks for watching", "thanks for watching!", "thank you for watching", "you", "bye", "bye.",
        "[blank_audio]", "[ silence ]", "(silence)", "[music]", "(music)", "[noise]", "(wind blowing)", "...", ".", "so", "okay.",
        "please subscribe", "subscribe to my channel", "[inaudible]"
    };

    /// <summary>Cleans recognizer output; null when it's a known hallucination or just the prompt read back.</summary>
    public static string? Clean(string? text, string prompt = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = System.Text.RegularExpressions.Regex.Replace(text, @"\[(?:BLANK_AUDIO|Music|MUSIC|Silence|SILENCE)\]|\((?:music|silence|wind|static)[^)]*\)", "").Trim();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");
        if (t.Length == 0 || Hallucinations.Contains(t) || Hallucinations.Contains(t.TrimEnd('.', '!', '?'))) return null;
        if (prompt.Length > 0 && t.Length > 20 && prompt.Contains(t.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) return null;
        return t;
    }
}

/// <summary>Local Whisper models (whisper.cpp ggml format, English-only, quantized).</summary>
public sealed record WhisperModelInfo(string Id, string Label, string FileName, long ApproxBytes, string Sha256)
{
    public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName}";

    // Checksums of the official files (computed by CI from huggingface.co/ggerganov/whisper.cpp).
    public static readonly WhisperModelInfo Accurate = new("small.en", "Accurate (small.en, ~180 MB, slower)", "ggml-small.en-q5_1.bin", 190_000_000,
        "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30");
    public static readonly WhisperModelInfo Fast = new("base.en", "Fast (base.en, ~60 MB)", "ggml-base.en-q5_1.bin", 60_000_000,
        "4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f");

    public static WhisperModelInfo For(bool accurate) => accurate ? Accurate : Fast;
}
