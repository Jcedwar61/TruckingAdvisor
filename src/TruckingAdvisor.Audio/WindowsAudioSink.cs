using System.Runtime.Versioning;
using System.Speech.Synthesis;
using NAudio.Wave;
using TruckingAdvisor.Audio.Dsp;
using TruckingAdvisor.Audio.Local;
using TruckingAdvisor.EdgeTts;
using TruckingAdvisor.Shared.Diagnostics;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;

namespace TruckingAdvisor.Audio;

/// <summary>
/// Plays an <see cref="AudioItem"/> and completes when playback really ends.
/// - WAV from OpenAI is played as-is.
/// - Otherwise text is spoken with a free Microsoft neural voice (same source as SimCoach), and with a
///   Windows voice when the neural voice can't be reached (offline, service down).
/// - CB items go through the radio filter plus key-up/squelch clicks (unless clean audio is on).
/// - Cancelling the token stops playback immediately (used for critical alerts).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAudioSink : IAudioSink
{
    private static readonly HashSet<string> FemaleVoices = new(StringComparer.OrdinalIgnoreCase)
        { "nova", "shimmer", "coral", "sage", "alloy", "ballad", "marin" };

    /// <summary>Accessibility option: skip static, filtering and clicks.</summary>
    public bool CleanAudio { get; set; }
    public float StaticLevel { get; set; } = 0.012f;
    public int WindowsVoiceRate { get; set; }
    /// <summary>Master mute: items complete immediately without sound.</summary>
    public bool Muted { get; set; }
    /// <summary>Output device per channel ("cb", "dispatcher", "advisor", ...). -1 = Windows default.</summary>
    public Func<string, int>? DeviceSelector { get; set; }

    /// <summary>Which voices to use for text (OpenAI audio arrives already rendered).</summary>
    public VoiceEngine Engine { get; set; } = VoiceEngine.FreeNeural;
    /// <summary>Neural voice for items without one (the co-driver), "voice|rate|pitch".</summary>
    public string DefaultNeuralVoice { get; set; } = "en-US-AndrewNeural|+0%|+0Hz";
    /// <summary>On-device voice (offline). Set once at startup; null keeps the network voice in use.</summary>
    public LocalVoiceSynthesizer? Local { get; set; }

    /// <summary>A voice couldn't be produced or played (shown to the player once in a while, always logged).</summary>
    public event Action<string>? VoiceProblem;

    /// <summary>Which voice spoke the last line ("Microsoft neural voice", "Windows voice", "OpenAI voice").</summary>
    public string LastVoiceSource { get; private set; } = "";

    private readonly EdgeTtsClient _edge = new() { Timeout = TimeSpan.FromSeconds(8) };
    private DateTime _neuralPausedUntil = DateTime.MinValue;
    private DateTime _lastProblemShown = DateTime.MinValue;

    private void Problem(string message, Exception? ex = null)
    {
        AppLog.Warn(ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");
        if (DateTime.UtcNow - _lastProblemShown < TimeSpan.FromMinutes(2)) return;
        _lastProblemShown = DateTime.UtcNow;
        VoiceProblem?.Invoke(message);
    }

    /// <summary>
    /// Turns an item into mono samples: OpenAI WAV, else a neural voice, else a Windows voice.
    /// Public so Settings can test voices and CI can check synthesis without a speaker.
    /// </summary>
    public async Task<(float[] Samples, int SampleRate, string Source)> RenderAsync(AudioItem item, CancellationToken ct)
    {
        // Explicit OpenAI audio and the chosen engine come first; the local voice and Windows voices take over.
        if (item.WavAudio is { Length: > 44 } audio)
        {
            try
            {
                // OpenAI voices arrive as MP3; the realtime mode hands over WAV. Tell them apart by their header.
                bool isWav = audio[0] == 'R' && audio[1] == 'I' && audio[2] == 'F' && audio[3] == 'F';
                var (s, r) = isWav ? DecodeWav(audio) : DecodeMp3(audio);
                if (s.Length > 0) return (s, r, "OpenAI voice");
                Problem("The OpenAI voice returned no audio; using a free voice instead.");
            }
            catch (Exception ex)
            {
                Problem("The OpenAI voice audio couldn't be decoded; using a free voice instead.", ex);
            }
        }
        if (Engine is VoiceEngine.Local or VoiceEngine.LocalKokoro && Local is { IsReady: true })
        {
            var (localVoice, localRate, _) = ParseVoice(item.NeuralVoice ?? DefaultNeuralVoice);
            try
            {
                // Resampling for pitch also changes the tempo, so ask the model for the difference back:
                // the line lands at the intended pace instead of getting faster the deeper the voice is.
                float pitch = PitchFactor(item.NeuralVoice ?? DefaultNeuralVoice);
                float speed = Math.Clamp(LocalSpeed(localRate) / pitch, 0.6f, 1.4f);
                var (s, r) = await Local.SynthesizeAsync(item.Text, LocalSpeaker(item.VoiceId, Engine), speed, ct);
                s = Normalize(PitchShift(s, r, pitch));
                if (s.Length > 0) return (s, r, "On-device voice");
                Problem("The on-device voice returned no audio; using a free voice instead.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Problem("The on-device voice failed; using a free voice instead.", ex);
            }
        }
        if (Engine != VoiceEngine.Windows && DateTime.UtcNow >= _neuralPausedUntil)
        {
            var (voice, rate, pitch) = ParseVoice(item.NeuralVoice ?? DefaultNeuralVoice);
            try
            {
                byte[] mp3 = await _edge.SynthesizeMp3Async(item.Text, voice, rate, pitch, ct);
                var (s, r) = DecodeMp3(mp3);
                if (s.Length > 0) return (s, r, "Microsoft neural voice");
                Problem("The free neural voice returned no audio; using a Windows voice.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Don't wait on a dead connection for every line: try the neural voice again in a minute.
                _neuralPausedUntil = DateTime.UtcNow.AddMinutes(1);
                Problem("Free neural voices aren't reachable right now (no internet?), so a Windows voice is speaking.", ex);
            }
        }
        try
        {
            // The Windows voices have their own pitch handling via the rate, so the pitch hint goes to it.
            float pitch = PitchFactor(item.NeuralVoice ?? DefaultNeuralVoice);
            var (s, r) = DecodeWav(await Task.Run(() => RenderWindowsVoice(item.Text, item.NeuralVoice ?? DefaultNeuralVoice, pitch), ct));
            return (s, r, "Windows voice");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Problem($"No voice could speak this line ({ex.Message}). Check Settings > Voices > Test voices.", ex);
            throw;
        }
    }

    /// <summary>
    /// Runs a line through everything playback does (voice, radio filter, clicks, volume, the float-to-bytes
    /// conversion the speaker gets) without a speaker, and returns how many bytes would be played. Used by the
    /// self-test so playback problems are caught on machines without audio hardware.
    /// </summary>
    public async Task<(long Bytes, string Source)> RenderPlaybackAsync(AudioItem item, float volume, CancellationToken ct)
    {
        var (samples, sampleRate, source) = await RenderAsync(item, ct);
        samples = Shape(item, samples, sampleRate, volume);
        var wave = new NAudio.Wave.SampleProviders.SampleToWaveProvider(new ArraySampleProvider(samples, sampleRate));
        var buf = new byte[wave.WaveFormat.AverageBytesPerSecond / 10];
        long total = 0;
        int n;
        while ((n = wave.Read(buf, 0, buf.Length)) > 0) total += n;
        return (total, source);
    }

    /// <summary>Levelling, radio filter, key-up / squelch clicks and volume (shared by playback and the self-test).</summary>
    private float[] Shape(AudioItem item, float[] samples, int sampleRate, float volume0To1)
    {
        samples = Normalize(samples);
        if (item.ApplyCbEffects && !CleanAudio)
        {
            var dsp = new CbRadioDspFilter { StaticLevel = StaticLevel };
            dsp.ProcessBuffer(samples, sampleRate);
            var click = dsp.GenerateMicClickAndSquelchTail(sampleRate);
            samples = [.. click, .. samples, .. click];
        }
        // Leave headroom so a loud line doesn't clip against the device.
        for (int i = 0; i < samples.Length; i++) samples[i] *= volume0To1 * 0.9f;
        return samples;
    }

    public static (string Voice, string Rate, string Pitch) ParseVoice(string spec)
    {
        var parts = (spec ?? "").Split('|');
        string voice = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "en-US-AndrewNeural";
        return (voice, parts.Length > 1 ? parts[1] : "+0%", parts.Length > 2 ? parts[2] : "+0Hz");
    }

    private static (float[] Samples, int Rate) DecodeWav(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.Channels == 2) provider = provider.ToMono();
        return (ReadAll(provider), provider.WaveFormat.SampleRate);
    }

    /// <summary>MP3 to mono floats with NLayer (pure .NET, no Windows codec needed).</summary>
    public static (float[] Samples, int Rate) DecodeMp3(byte[] mp3)
    {
        using var mpeg = new NLayer.MpegFile(new MemoryStream(mp3));
        int channels = mpeg.Channels, rate = mpeg.SampleRate;
        var all = new List<float>(rate * 4);
        var buf = new float[rate * channels];
        int n;
        while ((n = mpeg.ReadSamples(buf, 0, buf.Length)) > 0)
        {
            if (channels == 1) all.AddRange(buf.AsSpan(0, n).ToArray());
            else for (int i = 0; i + 1 < n; i += channels) all.Add((buf[i] + buf[i + 1]) * 0.5f);
        }
        return (all.ToArray(), rate);
    }

    public static IReadOnlyList<string> ListOutputDevices() =>
        Enumerable.Range(0, WaveOut.DeviceCount).Select(i => WaveOut.GetCapabilities(i).ProductName).ToList();

    public async Task PlayAsync(AudioItem item, float volume0To1, CancellationToken cancellationToken)
    {
        if (Muted) return;
        var (samples, sampleRate, source) = await RenderAsync(item, cancellationToken);
        LastVoiceSource = source;
        cancellationToken.ThrowIfCancellationRequested();

        samples = Shape(item, samples, sampleRate, volume0To1);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int device = DeviceSelector?.Invoke(item.Channel) ?? -1;
        using var output = new WaveOutEvent { DeviceNumber = device >= -1 && device < WaveOut.DeviceCount ? device : -1 };
        try
        {
            output.Init(new ArraySampleProvider(samples, sampleRate));
        }
        catch (Exception ex)
        {
            Problem($"Couldn't open the speaker for {item.Channel} audio ({ex.Message}). Check Settings > Audio devices.", ex);
            throw;
        }
        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null) done.TrySetException(e.Exception);
            else done.TrySetResult();
        };
        using var reg = cancellationToken.Register(() => output.Stop());
        output.Play();
        await done.Task;
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Brings every line to the same loudness. Voices differ a lot (a model may peak at 0.25, another at 0.7)
    /// and a line played quiet is as bad as one played loud, so lines are matched to a comfortable target.
    /// </summary>
    internal static float[] Normalize(float[] samples)
    {
        const float TargetPeak = 0.72f;
        if (samples.Length == 0) return samples;
        float peak = 0;
        for (int i = 0; i < samples.Length; i++) peak = Math.Max(peak, MathF.Abs(samples[i]));
        if (peak < 1e-4f) return samples;
        float gain = Math.Clamp(TargetPeak / peak, 0.05f, 25f);
        if (MathF.Abs(gain - 1f) < 0.02f) return samples;
        for (int i = 0; i < samples.Length; i++) samples[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
        return samples;
    }

    /// <summary>
    /// Voice ids are mapped to fixed speaker indexes so each character keeps their own voice offline.
    /// Supertonic has ten voices and Kokoro fifty-four; Kokoro gives every male regular a man and every
    /// female one a woman, and is the model that keeps all of them apart. Indexes come from measuring
    /// each speaker (pitch in Hz), not from the order the file happens to list them in.
    /// </summary>
    internal static int LocalSpeaker(string? voiceId, VoiceEngine engine = VoiceEngine.Local)
    {
        string id = (voiceId ?? "").ToLowerInvariant();
        if (engine == VoiceEngine.LocalKokoro)
        {
            return id switch
            {
                // Kokoro's table is af_*/am_*. Men, one each, chosen for depth that still stays clear:
                // am_echo 12 (124 Hz), bm_lewis 27 (99), am_michael 16 (128), am_puck 18 (136),
                // bm_fable 25 (141), am_liam 15 (139), bm_daniel 24 (136), bm_george 26 (158), am_adam 11 (131).
                "onyx" => 12, "ash" => 27, "ballad" => 11, "echo" => 15, "coral" => 16,
                "liam" => 24, "fable" => 26, "marin" => 25, "verse" => 18, "alloy" => 17,
                // Women (192-238 Hz): af_nova 7, af_sarah 9, af_sky 10, bf_alice 20, bf_emma 21.
                "nova" => 7, "sage" => 9, "shimmer" => 10, "jenny" => 20, "sonia" => 21,
                var other => KokoroVoiceModel.MaleVoices[StableSpeaker(other) % KokoroVoiceModel.MaleVoices.Length]
            };
        }
        return id switch
        {
            // Supertonic has ten voices and seven of them read male (9 = 93 Hz, 6 = 100, 7 = 109, 8 = 130,
            // 5 = 151, 4 = 167, 2 = 179), so three of the men share. The women keep the three left over.
            "onyx" => 9, "ash" => 6, "fable" => 7, "alloy" => 8, "ballad" => 5, "echo" => 4,
            "verse" => 2, "coral" => 9, "liam" => 6, "marin" => 5,
            "shimmer" => 0, "sage" => 0, "jenny" => 1, "sonia" => 3, "nova" => 3,
            var other => StableSpeaker(other) % 10
        };
    }

    private static int StableSpeaker(string? voiceId) => Math.Abs((voiceId ?? "").ToLowerInvariant() switch
    {
        "onyx" => 6,
        "echo" => 9,
        "fable" => 8,
        "alloy" => 4,
        "sage" => 5,
        "nova" => 3,
        "coral" => 1,
        "ash" => 7,
        "shimmer" => 2,
        "verse" => 0,
        var other => other.Length > 0 ? Math.Abs(other.Aggregate(17, (h, c) => unchecked(h * 31 + c))) : 5
    });

    /// <summary>The pitch offset in a "voice|rate|pitch" spec ("-8Hz") as a multiplier.</summary>
    private static float PitchFactor(string spec)
    {
        var parts = (spec ?? "").Split('|');
        if (parts.Length < 3) return 1f;
        var m = System.Text.RegularExpressions.Regex.Match(parts[2], @"^([+-]?[\d.]+)Hz$");
        if (!m.Success || !float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float hz)) return 1f;
        // Roughly 4.5 Hz per semitone at a 130 Hz speaking fundamental.
        return Math.Clamp(MathF.Pow(2f, hz / 12f / 4.5f), 0.8f, 1.25f);
    }

    /// <summary>
    /// Pitch shift by resampling; <paramref name="factor"/> is the pitch multiplier, so 0.9 is a deeper
    /// voice and 1.1 a higher one. That changes the tempo by 1/factor, which callers give back to the
    /// synthesizer. Shifting up skips input samples, which folds everything above the new Nyquist back in
    /// as buzz, so the input is low-passed first. One-pole cascades are plenty for a 20 % shift and cost
    /// nothing; the result is smoother than the raw nearest-neighbour decimation it replaces.
    /// </summary>
    internal static float[] PitchShift(float[] samples, int sampleRate, float factor)
    {
        if (MathF.Abs(factor - 1f) < 0.01f || samples.Length < 2) return samples;
        double step = factor;
        if (step > 1.02)
        {
            float cutoff = (float)(sampleRate * 0.45 / factor);
            float a = 1f - MathF.Exp(-2f * MathF.PI * cutoff / sampleRate);
            float z1 = samples[0], z2 = samples[0];
            var filtered = new float[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                z1 += (samples[i] - z1) * a;
                z2 += (z1 - z2) * a;
                filtered[i] = z2;
            }
            samples = filtered;
        }
        var output = new float[(int)(samples.Length / step)];
        for (int i = 0; i < output.Length; i++)
        {
            double pos = i * step;
            int j = (int)pos;
            float frac = (float)(pos - j);
            output[i] = j + 1 < samples.Length ? samples[j] * (1 - frac) + samples[j + 1] * frac : samples[Math.Min(j, samples.Length - 1)];
        }
        return output;
    }

    /// <summary>
    /// The on-device model already speaks at ~150 wpm, so slow-down hints only move it a little: the
    /// same -45% that tames a 280 wpm service voice lands the model at about 165 wpm here.
    /// </summary>
    internal static float LocalSpeed(string rate) => Math.Clamp(1f - (1f - RatePercent(rate)) * 0.15f, 0.7f, 1.35f);

    /// <summary>
    /// SpeechSynthesizer.Rate steps are worth about 12 % of pace each on the Windows voices (measured:
    /// 128 wpm at 0, 183 wpm at 3), while the neural services run near 255 wpm. This maps the line's pace
    /// onto the Windows scale so the fallback lands at the same speed as the neural voice instead of
    /// crawling. <paramref name="pitchFactor"/> is folded in because the caller's resampling changes tempo.
    /// </summary>
    internal static int WindowsRate(string rateSpec, float pitchFactor, int offset)
    {
        const float ReferenceWpm = 128f, Step = 1.128f, NeutralWpm = 265f;
        float target = NeutralWpm * RatePercent(rateSpec) / pitchFactor;
        int rate = (int)MathF.Round(MathF.Log(target / ReferenceWpm) / MathF.Log(Step));
        return Math.Clamp(rate + offset, -8, 8);
    }

    /// <summary>"+0%" / "-32%" (or a bare "0.92") to a speed multiplier.</summary>
    internal static float RatePercent(string rate)
    {
        var m = System.Text.RegularExpressions.Regex.Match(rate ?? "", @"^([+-]?[\d.]+)%?$");
        if (!m.Success || !float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v)) return 1f;
        // A percent is relative to normal speed ("-32%" -> 0.68); a bare number is already a multiplier.
        return Math.Clamp(m.Value.EndsWith('%') ? 1f + v / 100f : v, 0.5f, 1.5f);
    }

    /// <summary>
    /// Windows voices are the last resort, but they still get the line's pace, so a trucker doesn't
    /// suddenly read at machine speed on a bad connection.
    /// </summary>
    private byte[] RenderWindowsVoice(string text, string voiceSpec, float pitchFactor)
    {
        var (voiceId, rateSpec, _) = ParseVoice(voiceSpec);
        using var synth = new SpeechSynthesizer();
        try
        {
            synth.SelectVoiceByHints(voiceId is not null && FemaleVoices.Contains(voiceId) ? VoiceGender.Female : VoiceGender.Male);
        }
        catch { /* keep default voice */ }
        synth.Rate = WindowsRate(rateSpec, pitchFactor, WindowsVoiceRate);
        using var ms = new MemoryStream();
        synth.SetOutputToWaveStream(ms);
        synth.Speak(text);
        synth.SetOutputToNull();
        return ms.ToArray();
    }

    private static float[] ReadAll(ISampleProvider provider)
    {
        var all = new List<float>();
        var buffer = new float[provider.WaveFormat.SampleRate];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        return all.ToArray();
    }

    internal sealed class ArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;
        public ArraySampleProvider(float[] samples, int sampleRate)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _samples.Length - _position);
            // NAudio's SampleToWaveProvider hands Read a byte[] aliased as float[] (WaveBuffer), so
            // Array.Copy's array-type check fails with ArrayTypeMismatchException. Spans copy by memory.
            _samples.AsSpan(_position, n).CopyTo(buffer.AsSpan(offset, n));
            _position += n;
            return n;
        }
    }
}
