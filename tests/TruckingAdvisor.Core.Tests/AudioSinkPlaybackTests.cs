using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TruckingAdvisor.Audio;
using TruckingAdvisor.Audio.Dsp;
using TruckingAdvisor.Shared.Enums;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

/// <summary>
/// Regression: NAudio's SampleToWaveProvider passes the byte[] it hands to ISampleProvider.Read
/// through a WaveBuffer, whose float[] view is really a byte[]. Array.Copy in
/// ArraySampleProvider.Read rejected it with ArrayTypeMismatchException ("Source array type cannot
/// be assigned to destination array type"), on the playback thread, so no voice line ever played.
/// </summary>
public class AudioSinkPlaybackTests
{
    private static float[] Ramp(int length) => Enumerable.Range(0, length).Select(i => i / (float)length).ToArray();

    [Fact]
    public void Read_FillsTheByteBufferNaudioAliasesAsFloats()
    {
        var samples = Ramp(64);
        var provider = new WindowsAudioSink.ArraySampleProvider(samples, 24000);
        IWaveProvider waveProvider = new SampleToWaveProvider(provider);
        var bytes = new byte[samples.Length * 4];

        int read = waveProvider.Read(bytes, 0, bytes.Length);

        Assert.Equal(bytes.Length, read);
        var written = new float[samples.Length];
        Buffer.BlockCopy(bytes, 0, written, 0, bytes.Length);
        Assert.Equal(samples, written);
    }

    [Fact]
    public void Read_HonoursOffsetAndCount_AndReportsTheEnd()
    {
        var samples = Ramp(10);
        var provider = new WindowsAudioSink.ArraySampleProvider(samples, 24000);
        var buffer = new float[20];

        Assert.Equal(samples.Length, provider.Read(buffer, 3, buffer.Length));
        Assert.Equal(samples, buffer.Skip(3).Take(samples.Length));
        Assert.Equal(0, provider.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void Read_WorksOnAPlainFloatBuffer()
    {
        var samples = Ramp(5);
        var provider = new WindowsAudioSink.ArraySampleProvider(samples, 24000);
        var buffer = new float[samples.Length];

        Assert.Equal(samples.Length, provider.Read(buffer, 0, buffer.Length));
        Assert.Equal(samples, buffer);
    }
}

public class CbRadioFilterTests
{
    private static float[] Tone(int rate, double seconds, double hz, float amp = 0.4f) =>
        Enumerable.Range(0, (int)(seconds * rate)).Select(i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    private static float[] Bursts(int rate, float amp = 0.4f)
    {
        // 0.6 s of voice-like tone, 0.6 s of digital silence, twice: what a spoken line looks like.
        var s = new float[rate * 24 / 10];
        for (int i = 0; i < rate * 6 / 10; i++) s[i] = (float)(amp * Math.Sin(2 * Math.PI * 700 * i / rate));
        for (int i = rate * 12 / 10; i < rate * 18 / 10; i++) s[i] = (float)(amp * Math.Sin(2 * Math.PI * 700 * (i - rate * 12 / 10) / rate));
        return s;
    }

    [Fact]
    public void Hiss_Is_Quieter_Under_A_Voice_Than_In_Silence()
    {
        var filter = new CbRadioDspFilter();
        var samples = Bursts(24000);
        filter.ProcessBuffer(samples, 24000);

        double Rms(int from, int to)
        {
            double t = 0;
            for (int i = from; i < to; i++) t += samples[i] * (double)samples[i];
            return Math.Sqrt(t / (to - from));
        }

        // The second half of the gap (silence in, hiss out) versus inside the second burst (voice in, hiss out).
        double gap = Rms(24000 * 6 / 10 + 24000 * 3 / 10, 24000 * 12 / 10);
        double voice = Rms(24000 * 13 / 10, 24000 * 17 / 10);
        Assert.True(gap > 0, "the radio should still hiss in silence");
        Assert.True(gap < voice / 20, $"hiss should duck under the voice (gap {gap:F4}, voice {voice:F4})");
    }

    [Fact]
    public void Voice_Stays_In_The_Cb_Band()
    {
        var filter = new CbRadioDspFilter();
        var low = Tone(24000, 0.5, 70);    // engine rumble and road noise
        var high = Tone(24000, 0.5, 9000); // codec fizz
        var mid = Tone(24000, 0.5, 1200);  // voice
        filter.ProcessBuffer(low, 24000);
        filter.ProcessBuffer(high, 24000);
        filter.ProcessBuffer(mid, 24000);
        double peak(float[] s) => s.Skip(1200).Max(MathF.Abs);

        Assert.True(peak(mid) > peak(low) * 2.5, "a 1.2 kHz voice should survive the band pass, rumble should not");
        Assert.True(peak(mid) > peak(high) * 4, "9 kHz fizz should be cut away");
    }

    [Fact]
    public void Deep_Voice_Keeps_Its_Fundamental()
    {

        // A deep trucker speaks near 90 Hz; the old 145 Hz rumble cut left him thin. An octave-up harmonic
        // at 180 Hz must not end up louder than his fundamental.
        var filter = new CbRadioDspFilter();
        var low = Tone(24000, 0.5, 95, 0.3f);
        var harm = Tone(24000, 0.5, 190, 0.3f);
        filter.ProcessBuffer(low, 24000);
        filter.ProcessBuffer(harm, 24000);
        Assert.True(low.Skip(4800).Max(MathF.Abs) > harm.Skip(4800).Max(MathF.Abs) * 0.6f);
    }
    [Fact]
    public void Output_Does_Not_Clip_And_Clean_Audio_Bypasses_Everything()
    {
        var loud = Tone(24000, 0.2, 800, 0.95f);
        new CbRadioDspFilter().ProcessBuffer(loud, 24000);
        Assert.True(loud.Max(MathF.Abs) <= 0.999f);

        var untouched = Tone(24000, 0.1, 800, 0.95f);
        var copy = (float[])untouched.Clone();
        new CbRadioDspFilter { CleanAudioAccessibilityBypass = true }.ProcessBuffer(untouched, 24000);
        Assert.Equal(copy, untouched);
    }

    [Fact]
    public void Voice_Band_Keeps_Presence_That_A_Telephone_Band_Would_Cut()
    {
        // 3.8 kHz sits outside the old 300-3200 Hz slice; it should now survive, so voices don't sound
        // muffled and sibilance isn't swallowed.
        var high = Tone(24000, 0.4, 3800);
        var mid = Tone(24000, 0.4, 1000);
        new CbRadioDspFilter().ProcessBuffer(high, 24000);
        new CbRadioDspFilter().ProcessBuffer(mid, 24000);
        Assert.True(high.Skip(2400).Max(MathF.Abs) > mid.Skip(2400).Max(MathF.Abs) * 0.2f);
    }

    [Fact]
    public void Hisss_Is_Quieter_Than_Before_Under_A_Voice()
    {
        // The old filter sat at 0.012 static with a 1/4 floor under speech; the new one at 0.008 with 0.15.
        var samples = Bursts(24000);
        new CbRadioDspFilter().ProcessBuffer(samples, 24000);
        double Rms(int from, int to)
        {
            double t = 0;
            for (int i = from; i < to; i++) t += samples[i] * (double)samples[i];
            return Math.Sqrt(t / (to - from));
        }
        double gap = Rms(24000 * 9 / 10, 24000 * 12 / 10);
        Assert.InRange(20 * Math.Log10(gap), -60, -47);
    }

    [Fact]
    public void Hiss_Stays_Subtle_But_Audible()
    {
        // A normal voice sits near 0.05 rms. Hiss should stay well under it (no more wash) without vanishing.
        var silence = new float[24000];
        new CbRadioDspFilter().ProcessBuffer(silence, 24000);
        var tail = silence.Skip(2400).ToArray();
        double rms = Math.Sqrt(tail.Average(x => x * (double)x));
        Assert.InRange(rms, 0.0012, 0.006);
    }

    [Fact]
    public void Clicks_Are_Short_And_Not_Silent()
    {
        var click = new CbRadioDspFilter().GenerateMicClickAndSquelchTail(24000);
        Assert.InRange(click.Length, 480, 1200); // 20-50 ms
        Assert.True(click.Max(MathF.Abs) > 0.02f);
        Assert.True(MathF.Abs(click[0]) < MathF.Abs(click[click.Length / 10]), "it should fade in so it doesn't pop");
    }
}

public class VolumeAndRateTests
{
    [Fact]
    public void Normalize_Brings_Quiet_And_Loud_Lines_To_The_Same_Level()
    {
        var quiet = Enumerable.Repeat(0.06f, 100).ToArray();
        var loud = Enumerable.Repeat(0.95f, 100).ToArray();
        Assert.Equal(WindowsAudioSink.Normalize(quiet).Max(), WindowsAudioSink.Normalize(loud).Max(), 2);
        Assert.Equal(0.72f, WindowsAudioSink.Normalize(quiet).Max(), 2);
    }

    [Fact]
    public void Normalize_Leaves_Silence_Alone()
    {
        var silence = new float[50];
        Assert.All(WindowsAudioSink.Normalize(silence), v => Assert.Equal(0f, v));
    }

    [Theory]
    [InlineData("+0%", 1f)]
    [InlineData("-33%", 0.67f)]
    [InlineData("0.92", 0.92f)]
    public void RatePercent_Reads_Both_Forms(string spec, float expected) =>
        Assert.Equal(expected, WindowsAudioSink.RatePercent(spec), 2);

    [Fact]
    public void LocalSpeed_Keeps_The_Models_Natural_Pace()
    {
        // The model is already ~150 wpm, so a -45% hint must stay near 1x instead of becoming 0.55x.
        Assert.Equal(1f, WindowsAudioSink.LocalSpeed("+0%"), 2);
        Assert.InRange(WindowsAudioSink.LocalSpeed("-45%"), 0.85f, 0.95f);
    }

    [Theory]
    [InlineData("-48%", 1f)]
    [InlineData("-45%", 1f)]
    [InlineData("-45%", 0.92f)]
    [InlineData("-38%", 1f)]
    public void WindowsRate_Matches_The_Pace_Of_The_Trucker_Voices(string rate, float pitch)
    {
        // The old formula put a -45 % line at 93 wpm, half a beat behind the neural voice. Windows steps are
        // worth about 12 % each from a 128 wpm base, so every trucker pace must land at a trucker pace.
        static double Wpm(int steps) => 128 * Math.Pow(1.128, steps);
        Assert.InRange(Wpm(WindowsAudioSink.WindowsRate(rate, pitch, 0)), 120, 175);
    }

    [Fact]
    public void WindowsRate_Follows_The_User_Offset()
    {
        Assert.Equal(WindowsAudioSink.WindowsRate("-45%", 1f, 0) + 2, WindowsAudioSink.WindowsRate("-45%", 1f, 2));
        Assert.Equal(8, WindowsAudioSink.WindowsRate("+50%", 1.25f, 4));
    }

    [Theory]
    [InlineData(0.9f, 180)]
    [InlineData(1.1f, 220)]
    public void PitchShift_Moves_Pitch_In_The_Right_Direction(float factor, double expectedHz)
    {
        // A "deep" hint is -8 Hz; it must lower the voice, not raise it (the old step was inverted).
        int rate = 24000, n = 24000;
        var tone = new float[n];
        for (int i = 0; i < n; i++) tone[i] = (float)Math.Sin(2 * Math.PI * 200 * i / rate) * 0.5f;
        var shifted = WindowsAudioSink.PitchShift(tone, rate, factor);
        int a = shifted.Length / 3, b = a * 2, crossings = 0;
        for (int i = a + 1; i < b; i++) if (shifted[i - 1] < 0 != shifted[i] < 0) crossings++;
        double hz = crossings / 2.0 / ((b - a) / (double)rate);
        Assert.InRange(hz, expectedHz - 12, expectedHz + 12);
    }

    [Fact]
    public void PitchShift_Filters_Before_Upsampling()
    {
        // A 9 kHz tone at 24 kHz folds to 3 kHz when samples are skipped without a filter; the anti-alias
        // stage keeps the level sane instead of leaving a full-amplitude buzz.
        int rate = 24000, n = 24000;
        var tone = new float[n];
        for (int i = 0; i < n; i++) tone[i] = (float)Math.Sin(2 * Math.PI * 9000 * i / rate) * 0.5f;
        var shifted = WindowsAudioSink.PitchShift(tone, rate, 1.2f);
        Assert.InRange(shifted.Length, 19990, 20010);
        Assert.InRange(shifted.Skip(200).Max(MathF.Abs), 0.01f, 0.5f);
    }

    [Fact]
    public void PitchShift_Is_A_Pass_Through_Without_A_Pitch_Hint()
    {
        var s = new float[] { 0.1f, -0.2f, 0.3f };
        Assert.Same(s, WindowsAudioSink.PitchShift(s, 24000, 1f));
    }

    [Fact]
    public void LocalSpeaker_Maps_Personalities_To_Distinct_Voices()
    {
        // coral is a male id (Carolina Chrome is a man), so it belongs with the men. Every male regular
        // must get a man's voice and every female regular a woman's; Kokoro keeps all fifteen apart.
        var men = new[] { "onyx", "echo", "fable", "alloy", "ash", "verse", "ballad", "marin", "liam", "coral" };
        var women = new[] { "nova", "sage", "shimmer", "jenny", "sonia" };
        foreach (var engine in new[] { VoiceEngine.Local, VoiceEngine.LocalKokoro })
        {
            var male = men.Select(v => WindowsAudioSink.LocalSpeaker(v, engine)).ToArray();
            var female = women.Select(v => WindowsAudioSink.LocalSpeaker(v, engine)).ToArray();
            Assert.Equal(engine == VoiceEngine.LocalKokoro ? men.Length : 7, male.Distinct().Count());
            Assert.Equal(engine == VoiceEngine.LocalKokoro ? women.Length : 3, female.Distinct().Count());
            Assert.All(female, w => Assert.DoesNotContain(w, male));
        }
        Assert.All(men, v => Assert.InRange(WindowsAudioSink.LocalSpeaker(v), 0, 9));
        Assert.All(men, v => Assert.InRange(WindowsAudioSink.LocalSpeaker(v, VoiceEngine.LocalKokoro), 11, 52));
    }
}
