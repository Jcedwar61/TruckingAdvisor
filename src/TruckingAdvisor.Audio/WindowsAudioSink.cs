using System.Runtime.Versioning;
using System.Speech.Synthesis;
using NAudio.Wave;
using TruckingAdvisor.Audio.Dsp;
using TruckingAdvisor.Shared.Interfaces;

namespace TruckingAdvisor.Audio;

/// <summary>
/// Plays an <see cref="AudioItem"/> and completes when playback really ends.
/// - WAV from OpenAI is played as-is; text is rendered with a free Windows voice.
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

    public static IReadOnlyList<string> ListOutputDevices() =>
        Enumerable.Range(0, WaveOut.DeviceCount).Select(i => WaveOut.GetCapabilities(i).ProductName).ToList();

    public async Task PlayAsync(AudioItem item, float volume0To1, CancellationToken cancellationToken)
    {
        if (Muted) return;
        byte[] wav = item.WavAudio ?? await Task.Run(() => RenderWindowsVoice(item.Text, item.VoiceId), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        float[] samples;
        int sampleRate;
        using (var reader = new WaveFileReader(new MemoryStream(wav)))
        {
            ISampleProvider provider = reader.ToSampleProvider();
            if (provider.WaveFormat.Channels == 2) provider = provider.ToMono();
            sampleRate = provider.WaveFormat.SampleRate;
            samples = ReadAll(provider);
        }

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
        output.Init(new ArraySampleProvider(samples, sampleRate));
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
