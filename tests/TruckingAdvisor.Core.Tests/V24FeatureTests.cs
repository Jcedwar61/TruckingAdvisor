using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TruckingAdvisor.AI;
using TruckingAdvisor.Core.Mapping;
using TruckingAdvisor.Data;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public sealed class V24FeatureTests
{
    // ---- PDF ----

    [Fact]
    public void Pdf_HasValidStructure_AndCorrectXrefOffsets()
    {
        var trip = new DeliveryTripRecord { Cargo = "Lumber (kiln dried)", Origin = "Phoenix", Destination = "Denver", DistanceMiles = 426, State = DeliveryState.Completed };
        byte[] pdf = PdfReportWriter.DeliveryReport(trip);
        string text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains(@"Lumber \(kiln dried\)", text); // parentheses escaped

        int startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        Assert.StartsWith("xref", text[startxref..]);
        var offsets = Regex.Matches(text[startxref..], @"(\d{10}) 00000 n").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        for (int i = 0; i < offsets.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj", text[offsets[i]..]);
    }

    [Fact]
    public void Pdf_PaginatesLongHistories()
    {
        var trips = Enumerable.Range(0, 150).Select(i => new DeliveryTripRecord { Cargo = $"Load {i}", Destination = "Reno", StartedAt = DateTimeOffset.UnixEpoch.AddDays(i) });
        string text = Encoding.Latin1.GetString(PdfReportWriter.TripHistory(trips, "Highway Ghost", "Co"));
        int pages = Regex.Matches(text, @"/Type /Page\b").Count;
        Assert.True(pages >= 3, $"expected several pages, got {pages}");
        Assert.Contains($"/Count {pages}", text);
    }

    [Fact]
    public void Pdf_EscapesNonLatinCharacters() => Assert.Equal(@"A\\B \(x\) ?", PdfReportWriter.Escape("A\\B (x) →"));

    // ---- Learned location & grades ----

    [Fact]
    public void Location_LearnedFromDeliveries_WithStateWhenUnambiguous()
    {
        var mem = new LocationMemory();
        Assert.Null(mem.Describe(1000, 1000));
        mem.Learn("Denver", -30000, -20000);
        mem.Learn("Denver", -30200, -20100);
        mem.Learn("Carlsbad", 5000, 9000);
        Assert.Equal("in Denver, CO", mem.Describe(-30100, -20000));
        Assert.Equal("near Denver, CO", mem.Describe(-30100 + 8000, -20000));
        Assert.Null(mem.Describe(-30100 + 40000, -20000));       // too far: unknown, not guessed
        Assert.Equal("in Carlsbad", mem.Describe(5000, 9000));     // CA and NM both have one: no state
        mem.Learn("Denver", 90000, 90000);                         // wildly off: ignored
        Assert.Equal("in Denver, CO", mem.Describe(-30100, -20000));
    }

    [Theory]
    [InlineData("Phoenix", "AZ")]
    [InlineData("Salt Lake City", "UT")]
    [InlineData("Springfield", null)]
    [InlineData("Kansas City", null)]
    [InlineData("Salina", null)]
    [InlineData("Nowhereville", null)]
    public void CityStates_NeverGuessAmbiguousNames(string city, string? state) => Assert.Equal(state, CityStates.StateFor(city));

    [Fact]
    public void Grades_WarnOnlyAheadInSameDirection()
    {
        var g = new GradeMemory();
        // Drove north (heading 0 = -Z) down a 7% grade around z = -1000.
        for (double z = -900; z >= -1100; z -= 50) g.Record(0, z, -7f, 0f, 50f);
        g.Record(0, -5000, -2f, 0f, 50f);                      // gentle: not recorded
        Assert.NotNull(g.Ahead(0, 0, 0f));                    // heading north toward it
        Assert.Equal(-7f, g.Ahead(0, 0, 0f)!.Value.Grade);
        Assert.Null(g.Ahead(0, 0, 180f));                     // heading south: not ahead
        Assert.Null(g.Ahead(0, -2000, 180f));                 // driving it uphill the other way: no warning
        Assert.Null(g.Ahead(5000, 0, 0f));                    // other road
        Assert.Null(new GradeMemory().Ahead(0, 0, 0f));
    }

    // ---- Audio helpers ----

    [Fact]
    public void Wav_ResamplesTo24k_AndRoundTrips()
    {
        var pcm16k = new byte[16000 * 2]; // 1 s of silence-ish ramp
        for (int i = 0; i < 16000; i++) { short v = (short)(i % 2000); pcm16k[i * 2] = (byte)v; pcm16k[i * 2 + 1] = (byte)(v >> 8); }
        byte[] wav16 = WavAudio.FromPcm16Mono(pcm16k, 16000);
        byte[] pcm24 = WavAudio.ToPcm16Mono(wav16, 24000);
        Assert.Equal(24000 * 2, pcm24.Length);
        Assert.Equal(1.0, OpenAiClientService.WavDurationSeconds(WavAudio.FromPcm16Mono(pcm24, 24000)), 2);
        Assert.Empty(WavAudio.ToPcm16Mono(new byte[10], 24000));
    }

    private sealed class FakeStt : ISpeechToText
    {
        public bool IsAvailable { get; set; }
        public string? Result;
        public int Calls;
        public Task<string?> TranscribeAsync(byte[] wav, CancellationToken ct) { Calls++; return Task.FromResult(Result); }
    }

    [Fact]
    public async Task OfflineRecognition_IsUsedWhenOpenAiUnavailableOrEmpty()
    {
        var openAi = new FakeStt { IsAvailable = false };
        var windows = new FakeStt { IsAvailable = true, Result = "breaker one nine" };
        var stt = new FallbackSpeechToText(openAi, windows);
        Assert.Equal("breaker one nine", await stt.TranscribeAsync(new byte[100], default));
        Assert.Equal("Windows (offline)", stt.LastUsed);

        openAi.IsAvailable = true; openAi.Result = "";            // online but heard nothing -> try offline
        Assert.Equal("breaker one nine", await stt.TranscribeAsync(new byte[100], default));
        openAi.Result = "hello there";
        Assert.Equal("hello there", await stt.TranscribeAsync(new byte[100], default));
        Assert.Equal("OpenAI", stt.LastUsed);
    }

    // ---- Realtime speech-to-speech ----

    private sealed class FakeTransport : IRealtimeTransport
    {
        public readonly List<string> Sent = new();
        private readonly Queue<string?> _incoming;
        public Uri? Uri;
        public FakeTransport(params string?[] incoming) => _incoming = new Queue<string?>(incoming);
        public Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct) { Uri = uri; return Task.CompletedTask; }
        public Task SendAsync(string json, CancellationToken ct) { Sent.Add(json); return Task.CompletedTask; }
        public Task<string?> ReceiveAsync(CancellationToken ct) => Task.FromResult(_incoming.Count > 0 ? _incoming.Dequeue() : null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string AudioDelta(string type) =>
        new JsonObject { ["type"] = type, ["delta"] = Convert.ToBase64String(new byte[4800]) }.ToJsonString();

    private static byte[] OneSecondWav() => WavAudio.FromPcm16Mono(new byte[32000], 16000);

    [Theory]
    [InlineData("response.output_audio.delta", "response.output_audio_transcript.done")]  // GA names
    [InlineData("response.audio.delta", "response.audio_transcript.done")]                // beta names
    public async Task Realtime_TranscribesThenAnswersInVoice(string audioEvent, string transcriptEvent)
    {
        var t = new FakeTransport(
            """{"type":"session.updated"}""",
            """{"type":"conversation.item.input_audio_transcription.completed","transcript":" Anybody out there? "}""",
            AudioDelta(audioEvent), AudioDelta(audioEvent),
            new JsonObject { ["type"] = transcriptEvent, ["transcript"] = "Yeah driver, I copy." }.ToJsonString(),
            """{"type":"response.done","response":{"status":"completed","usage":{"input_tokens":50,"output_tokens":20}}}""");
        var settings = new AiSettings { RealtimeModel = "gpt-realtime-mini" };
        var usage = new AiUsageTracker();
        await using var s = await RealtimeVoiceSession.StartAsync(t, settings, usage, "sk-test", "be a trucker", "ash", default);

        Assert.Equal("Anybody out there?", await s.SendPlayerAudioAsync(OneSecondWav(), default));
        var reply = await s.ReplyAsync(160, default);

        Assert.Equal("Yeah driver, I copy.", reply!.Text);
        Assert.Equal(44 + 9600, reply.WavAudio.Length);
        Assert.Equal("wss://api.openai.com/v1/realtime?model=gpt-realtime-mini", t.Uri!.ToString());
        Assert.Contains(t.Sent, m => m.Contains("\"session.update\"") && m.Contains("\"rate\":24000") && m.Contains("\"turn_detection\":null"));
        Assert.Contains(t.Sent, m => m.Contains("input_audio_buffer.commit"));
        Assert.True(usage.Data.Today.Requests >= 2);
    }

    [Fact]
    public async Task Realtime_ReportsServerErrors()
    {
        var t = new FakeTransport("""{"type":"error","error":{"message":"invalid model"}}""");
        await using var s = await RealtimeVoiceSession.StartAsync(t, new AiSettings(), new AiUsageTracker(), "k", "x", "ash", default);
        Assert.Null(await s.SendPlayerAudioAsync(OneSecondWav(), default));
        Assert.Contains("invalid model", s.LastError);
    }

    private sealed class Audio : IAudioPriorityManager
    {
        public readonly List<AudioItem> Items = new();
        public bool IsRadioChannelBusy { get; set; }
        public void EnqueueAnnouncement(string text, AudioPriority p, string v = "advisor", bool fx = false) => Enqueue(new AudioItem(text, p, v, fx));
        public void Enqueue(AudioItem item) { lock (Items) Items.Add(item); }
        public Task EnqueueAndWaitAsync(AudioItem item, CancellationToken ct = default) { Enqueue(item); return Task.CompletedTask; }
        public void SetChannelVolume(string c, float v) { }
        public void StopAll() { }
    }

    private static CBRadioService RealtimeRadio(Audio audio, Func<IRealtimeTransport> factory) =>
        new(PersonalityCatalog.CreateDefaultRoster(), new VirtualCompanyProfile { PlayerHandle = "Highway Ghost" }, audio,
            () => new AtsTelemetrySnapshot(), new AiSettings { GlobalAiEnabled = true, VoicePipeline = VoicePipeline.Realtime })
        {
            ReplyPause = (0, 0),
            RealtimeTransportFactory = factory,
            RealtimeApiKey = () => "sk-test",
            RealtimeUsage = new AiUsageTracker()
        };

    [Fact]
    public async Task Radio_UsesRealtimeVoice_AndSkipsSeparateTts()
    {
        var audio = new Audio();
        var radio = RealtimeRadio(audio, () => new FakeTransport(
            """{"type":"conversation.item.input_audio_transcription.completed","transcript":"Big Red, you out there?"}""",
            AudioDelta("response.output_audio.delta"),
            """{"type":"response.output_audio_transcript.done","transcript":"I'm here, driver."}""",
            """{"type":"response.done","response":{"status":"completed"}}"""));
        radio.BeginPushToTalk();
        await radio.EndPushToTalkWithAudioAsync(OneSecondWav());
        var item = Assert.Single(audio.Items);
        Assert.Equal("I'm here, driver.", item.Text);
        Assert.NotNull(item.WavAudio);
        Assert.Equal("Big Red", radio.ActivePartner!.Handle);
        Assert.Equal(CBRadioState.RadioIdle, radio.CurrentState);
    }

    [Fact]
    public async Task Radio_RealtimeCommand_IsHandledWithoutAReply()
    {
        var audio = new Audio();
        var transport = new FakeTransport("""{"type":"conversation.item.input_audio_transcription.completed","transcript":"TruckingAdvisor, switch to channel 21"}""");
        var radio = RealtimeRadio(audio, () => transport);
        VoiceCommand? cmd = null;
        radio.CommandReceived += (_, c) => cmd = c;
        radio.BeginPushToTalk();
        await radio.EndPushToTalkWithAudioAsync(OneSecondWav());
        Assert.Equal(21, radio.ActiveChannel);
        Assert.Equal(VoiceCommandKind.SwitchChannel, cmd!.Kind);
        Assert.Empty(audio.Items);
        Assert.DoesNotContain(transport.Sent, m => m.Contains("response.create"));
    }

    [Fact]
    public async Task Radio_FallsBackWhenRealtimeReplyFails()
    {
        var audio = new Audio();
        var radio = RealtimeRadio(audio, () => new FakeTransport(
            """{"type":"conversation.item.input_audio_transcription.completed","transcript":"Breaker one-nine, anybody got a copy?"}""",
            """{"type":"error","error":{"message":"rate limited"}}"""));
        string? notice = null;
        radio.Notice += (_, n) => notice = n;
        radio.BeginPushToTalk();
        await radio.EndPushToTalkWithAudioAsync(OneSecondWav());
        var item = Assert.Single(audio.Items);               // offline reply still goes out
        Assert.Null(item.WavAudio);
        Assert.Contains("standard voice pipeline", notice);
    }
}
