using System.Net;
using TruckingAdvisor.Core.Speech;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public class SpeechTests
{
    private static float[] Tone(double seconds, float amp, int rate = 16000, double freq = 220) =>
        Enumerable.Range(0, (int)(seconds * rate)).Select(i => (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate))).ToArray();

    private static float[] Noise(double seconds, float amp, int rate = 16000, int seed = 1)
    {
        var r = new Random(seed);
        return Enumerable.Range(0, (int)(seconds * rate)).Select(_ => (float)((r.NextDouble() * 2 - 1) * amp)).ToArray();
    }

    [Fact]
    public void Silence_IsFlagged_SoItIsNeverSent()
    {
        var (_, info) = SpeechAudio.Prepare(Noise(2, 0.002f), 16000);
        Assert.True(info.LooksSilent);
    }

    [Fact]
    public void QuietSpeech_IsTrimmed_AndBoosted()
    {
        // 0.5 s room noise, 1.2 s quiet "speech", 0.8 s room noise, with a DC offset.
        var clip = Noise(0.5, 0.001f).Concat(Tone(1.2, 0.05f)).Concat(Noise(0.8, 0.001f, seed: 2)).Select(v => v + 0.02f).ToArray();
        var (samples, info) = SpeechAudio.Prepare(clip, 16000);
        Assert.False(info.LooksSilent);
        Assert.InRange(samples.Length / 16000.0, 1.2, 1.8);          // trimmed, with margins
        Assert.InRange(samples.Max(MathF.Abs), 0.6f, 0.75f);        // boosted to about -3 dBFS
        Assert.True(info.Gain > 5);
        Assert.InRange(samples.Average(), -0.01, 0.01);             // DC offset removed
    }

    [Fact]
    public void Wav_RoundTrips_AndOtherRatesAreResampled()
    {
        var tone = Tone(1, 0.5f, 48000);
        var (back, rate) = SpeechAudio.ReadWav(SpeechAudio.ToWav(tone, 48000));
        Assert.Equal(48000, rate);
        Assert.Equal(tone.Length, back.Length);
        var (samples, _) = SpeechAudio.Prepare(back, 48000);
        Assert.InRange(samples.Length, 15000, 17500);                // ~1 s at 16 kHz
    }

    [Fact]
    public void UnfinishedWavHeader_StillReads()
    {
        var wav = SpeechAudio.ToWav(Tone(0.5, 0.5f));
        BitConverter.GetBytes(-1).CopyTo(wav, 40);                   // data size never filled in
        var (s, _) = SpeechAudio.ReadWav(wav);
        Assert.Equal(8000, s.Length);
    }

    [Theory]
    [InlineData("Thank you.", null)]
    [InlineData(" [BLANK_AUDIO] ", null)]
    [InlineData("you", null)]
    [InlineData("Thanks for watching!", null)]
    [InlineData("Breaker one-nine, Big Red, you got your ears on?", "Breaker one-nine, Big Red, you got your ears on?")]
    [InlineData("Thank you, Big Red, good copy.", "Thank you, Big Red, good copy.")]
    public void Hallucinations_AreDropped_RealSpeechKept(string heard, string? expected) =>
        Assert.Equal(expected, SpeechText.Clean(heard, SpeechText.BuildPrompt(new[] { "Big Red" })));

    [Fact]
    public void Prompt_ReadBack_IsDropped()
    {
        string prompt = SpeechText.BuildPrompt(new[] { "Big Red", "Highway Hank" });
        Assert.Null(SpeechText.Clean("CB radio, breaker one-nine, ten-four, copy that, come back, good buddy", prompt));
        Assert.Contains("Highway Hank", prompt);
        Assert.Contains("TruckingAdvisor", prompt);
    }

    private sealed class FakeHub : HttpMessageHandler
    {
        public byte[] File = Enumerable.Range(0, 2_000_000).Select(i => (byte)(i % 251)).ToArray();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            long from = r.Headers.Range?.Ranges.First().From ?? 0;
            var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new ByteArrayContent(File[(int)from..]), RequestMessage = r };
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task ModelDownload_ChecksTheChecksum_AndResumes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ta-models-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hub = new FakeHub();
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(hub.File)).ToLowerInvariant();
            var good = WhisperModelInfo.Fast with { Sha256 = sha };
            var dl = new ModelDownloader(dir, hub);

            Directory.CreateDirectory(dir);
            System.IO.File.WriteAllBytes(dl.PathFor(good) + ".partial", hub.File[..700_000]);   // earlier interrupted download
            string path = await dl.EnsureAsync(good);
            Assert.Equal(hub.File, System.IO.File.ReadAllBytes(path));
            Assert.True(dl.IsReady(good));

            var bad = WhisperModelInfo.Accurate with { Sha256 = new string('0', 64) };
            await Assert.ThrowsAsync<InvalidDataException>(() => dl.EnsureAsync(bad));
            Assert.False(System.IO.File.Exists(dl.PathFor(bad)));
            Assert.False(System.IO.File.Exists(dl.PathFor(bad) + ".partial"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
