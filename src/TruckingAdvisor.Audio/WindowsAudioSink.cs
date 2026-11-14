using System.Runtime.Versioning;
using System.Speech.Synthesis;
using NAudio.Wave;
using TruckingAdvisor.Audio.Dsp;
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
    public float StaticLevel { get; set; } = 0.03f;
    public int WindowsVoiceRate { get; set; }
    /// <summary>Master mute: items complete immediately without sound.</summary>
    public bool Muted { get; set; }
    /// <summary>Output device per channel ("cb", "dispatcher", "advisor", ...). -1 = Windows default.</summary>
    public Func<string, int>? DeviceSelector { get; set; }

    /// <summary>Which voices to use for text (OpenAI audio arrives already rendered).</summary>
    public VoiceEngine Engine { get; set; } = VoiceEngine.FreeNeural;
    /// <summary>Neural voice for items without one (the co-driver), "voice|rate|pitch".</summary>
    public string DefaultNeuralVoice { get; set; } = "en-US-AndrewNeural|+0%|+0Hz";

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
        if (item.WavAudio is { Length: > 44 } wav)
        {
            var (s, r) = DecodeWav(wav);
            return (s, r, "OpenAI voice");
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
            var (s, r) = DecodeWav(await Task.Run(() => RenderWindowsVoice(item.Text, item.VoiceId), ct));
            return (s, r, "Windows voice");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Problem($"No voice could speak this line ({ex.Message}). Check Settings > Voices > Test voices.", ex);
            throw;
        }
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

        if (item.ApplyCbEffects && !CleanAudio)
        {
            var dsp = new CbRadioDspFilter { StaticLevel = StaticLevel };
            dsp.ProcessBuffer(samples, sampleRate);
            var click = dsp.GenerateMicClickAndSquelchTail(sampleRate);
            samples = [.. click, .. samples, .. click];
        }

        for (int i = 0; i < samples.Length; i++) samples[i] *= volume0To1;

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

    private byte[] RenderWindowsVoice(string text, string? voiceId)
    {
        using var synth = new SpeechSynthesizer();
        try
        {
            synth.SelectVoiceByHints(voiceId is not null && FemaleVoices.Contains(voiceId) ? VoiceGender.Female : VoiceGender.Male);
        }
        catch { /* keep default voice */ }
        synth.Rate = Math.Clamp(WindowsVoiceRate, -10, 10);
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

    private sealed class ArraySampleProvider : ISampleProvider
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
            Array.Copy(_samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
