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

public class GameEventTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static TruckingAdvisor.Shared.Models.AtsTelemetrySnapshot Live(double seconds) => new()
    {
        ConnectionState = TruckingAdvisor.Shared.Enums.ConnectionState.Connected, Timestamp = T0.AddSeconds(seconds),
        TruckId = "truck.1", FuelGallons = 100, FuelCapacityGallons = 200, SpeedMph = 60, GameId = 2
    };

    [Fact]
    public void Fine_IsReportedOnce_WithTheAmountWrittenAfterTheFlagFlips()
    {
        var d = new TruckingAdvisor.Core.Events.GameEventDetector();
        var events = new List<TruckingAdvisor.Core.Events.GameEvent>();
        d.EventOccurred += (_, e) => events.Add(e);

        d.Update(Live(0) with { FinedFlag = true, FineAmount = 100, FineOffence = "speeding" }); // state from before we started: not an event
        d.Update(Live(1) with { FinedFlag = true, FineAmount = 100, FineOffence = "speeding" });
        Assert.Empty(events);
        d.Update(Live(2) with { FinedFlag = false, FineAmount = 100, FineOffence = "speeding" }); // flag flips, amount still the old one
        Assert.Empty(events);
        d.Update(Live(3) with { FinedFlag = false, FineAmount = 750, FineOffence = "red_signal" });
        d.Update(Live(4) with { FinedFlag = false, FineAmount = 750, FineOffence = "red_signal" });

        var fine = Assert.Single(events);
        Assert.Equal(TruckingAdvisor.Core.Events.GameEventKind.Fine, fine.Kind);
        Assert.Equal(750, fine.Amount);
        Assert.Equal("running a red light", TruckingAdvisor.Core.Events.GameEvent.OffenceText(fine.Detail));
    }

    [Fact]
    public void TollFerryAndRefuel_AreDetected_ButNotFromDemoDataOrATruckSwap()
    {
        var d = new TruckingAdvisor.Core.Events.GameEventDetector();
        var events = new List<TruckingAdvisor.Core.Events.GameEvent>();
        d.EventOccurred += (_, e) => events.Add(e);

        d.Update(Live(0));
        d.Update(Live(1) with { TollgateFlag = true });
        d.Update(Live(2) with { TollgateFlag = true, TollAmount = 12 });
        d.Update(Live(3) with { TollgateFlag = true, TollAmount = 12, FerryFlag = true });
        d.Update(Live(4) with { TollgateFlag = true, TollAmount = 12, FerryFlag = true, FerryAmount = 300, FerryRoute = "Dover to Calais" });
        Assert.Equal(new[] { TruckingAdvisor.Core.Events.GameEventKind.Toll, TruckingAdvisor.Core.Events.GameEventKind.Ferry }, events.Select(e => e.Kind));
        Assert.Equal("Dover to Calais", events[1].Detail);

        // Refuel: stopped, tank rises 100 -> 180 over a few frames, then settles.
        events.Clear();
        var flags = Live(0) with { TollgateFlag = true, TollAmount = 12, FerryFlag = true, FerryAmount = 300, SpeedMph = 0 };
        d.Update(flags with { Timestamp = T0.AddSeconds(5) });
        for (int i = 1; i <= 8; i++) d.Update(flags with { Timestamp = T0.AddSeconds(5 + i), FuelGallons = 100 + i * 10 });
        d.Update(flags with { Timestamp = T0.AddSeconds(14), FuelGallons = 180 });
        Assert.Empty(events); // not settled yet
        d.Update(flags with { Timestamp = T0.AddSeconds(18), FuelGallons = 180 });
        var refuel = Assert.Single(events);
        Assert.Equal(80f, refuel.Gallons, 1);

        // Switching to another truck with a fuller tank is not a refuel; demo data never raises anything.
        events.Clear();
        d.Update(flags with { Timestamp = T0.AddSeconds(20), TruckId = "truck.2", FuelGallons = 250 });
        d.Update(flags with { Timestamp = T0.AddSeconds(25), TruckId = "truck.2", FuelGallons = 250 });
        d.Update(new TruckingAdvisor.Shared.Models.AtsTelemetrySnapshot { IsDemoData = true, FinedFlag = true });
        d.Update(flags with { Timestamp = T0.AddSeconds(30), FinedFlag = true }); // first frame after reconnect = new baseline
        d.Update(flags with { Timestamp = T0.AddSeconds(31), FinedFlag = true });
        Assert.Empty(events);
    }

    [Fact]
    public void Trip_AddsUpFinesTollsAndFares_IntoNetIncome()
    {
        var tracker = new TruckingAdvisor.Core.Trips.TripTracker();
        TruckingAdvisor.Shared.Models.DeliveryTripRecord? finished = null;
        tracker.TripFinished += (_, t) => finished = t;
        var job = Live(0) with { JobActive = true, JobIncome = 12000, CargoName = "Lumber", DestinationCity = "Denver" };
        tracker.Update(Live(-1));
        tracker.Update(job);
        tracker.RecordGameEvent(new(TruckingAdvisor.Core.Events.GameEventKind.Fine, 750, "red_signal", 0, T0));
        tracker.RecordGameEvent(new(TruckingAdvisor.Core.Events.GameEventKind.Fine, 300, "speeding", 0, T0));
        tracker.RecordGameEvent(new(TruckingAdvisor.Core.Events.GameEventKind.Toll, 25, "", 0, T0));
        tracker.RecordGameEvent(new(TruckingAdvisor.Core.Events.GameEventKind.Ferry, 400, "A to B", 0, T0));
        tracker.Update(job with { Timestamp = T0.AddSeconds(10), JobActive = false, JobDeliveredFlag = true });
        tracker.Update(job with { Timestamp = T0.AddSeconds(10.05), JobActive = false, JobDeliveredFlag = true, DeliveredRevenue = 12400, DeliveredEarnedXp = 310 });

        Assert.Equal(12400, finished!.PaidRevenue);
        Assert.Equal(1050, finished.Fines);
        Assert.Equal(2, finished.FineCount);
        Assert.Equal(12400 - 1050 - 25 - 400, finished.NetIncome);
        Assert.Contains("running a red light", finished.FineOffences);
    }

    [Fact]
    public void Parser_ReadsGameplayEventFields_AtThePluginOffsets()
    {
        var block = new byte[TruckingAdvisor.Telemetry.ScsTelemetryLayout.RequiredBytes];
        block[TruckingAdvisor.Telemetry.ScsTelemetryLayout.SdkActive] = 1;
        BitConverter.GetBytes(12u).CopyTo(block, TruckingAdvisor.Telemetry.ScsTelemetryLayout.PluginRevision);
        block[4304] = 1; block[4305] = 1;                          // fined, tollgate
        BitConverter.GetBytes(750L).CopyTo(block, 4216);           // fineAmount
        BitConverter.GetBytes(12L).CopyTo(block, 4224);            // tollgatePayAmount
        BitConverter.GetBytes(12400L).CopyTo(block, 4208);         // jobDeliveredRevenue
        BitConverter.GetBytes(900L).CopyTo(block, 4200);           // jobCancelledPenalty
        BitConverter.GetBytes(310).CopyTo(block, 640);             // jobDeliveredEarnedXp
        System.Text.Encoding.ASCII.GetBytes("red_signal").CopyTo(block, 3436);
        System.Text.Encoding.ASCII.GetBytes("Dover").CopyTo(block, 3468);
        System.Text.Encoding.ASCII.GetBytes("Calais").CopyTo(block, 3532);

        Assert.Equal(TruckingAdvisor.Telemetry.ParseResult.Ok, TruckingAdvisor.Telemetry.ScsTelemetryParser.TryParse(block, T0, out var s));
        Assert.True(s.FinedFlag);
        Assert.True(s.TollgateFlag);
        Assert.False(s.FerryFlag);
        Assert.Equal(750, s.FineAmount);
        Assert.Equal("red_signal", s.FineOffence);
        Assert.Equal(12, s.TollAmount);
        Assert.Equal(12400, s.DeliveredRevenue);
        Assert.Equal(900, s.CancelledPenalty);
        Assert.Equal(310, s.DeliveredEarnedXp);
        Assert.Equal("Dover to Calais", s.FerryRoute);
    }

    [Fact]
    public void Recording_RoundTrips_AndSurvivesBeingCutOff()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ta-rec-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "session.tarec");
        try
        {
            var block = new byte[TruckingAdvisor.Telemetry.ScsTelemetryLayout.RequiredBytes];
            using (var rec = new TruckingAdvisor.Telemetry.TelemetryRecorder(path))
            {
                for (int i = 0; i < 50; i++) { block[100] = (byte)i; rec.Write(T0.AddMilliseconds(200 * i), block); }
                Assert.Equal(50, rec.Frames);
            }
            var frames = TruckingAdvisor.Telemetry.TelemetryRecorder.Read(path).ToList();
            Assert.Equal(50, frames.Count);
            Assert.Equal(49, frames[^1].Block[100]);
            Assert.Equal(T0.AddMilliseconds(200 * 49), frames[^1].Time);
            Assert.True(new FileInfo(path).Length < 50_000); // compressed

            // A file cut off mid-write still gives the frames before the cut.
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..(bytes.Length * 2 / 3)]);
            var partial = TruckingAdvisor.Telemetry.TelemetryRecorder.Read(path).ToList();
            Assert.InRange(partial.Count, 1, 49);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Log_ScrubsUserNameProfilePathAndKeys()
    {
        string s = TruckingAdvisor.Shared.Diagnostics.AppLog.Scrub(
            @"Found C:\Users\JaneDoe\AppData\Local\X and D:\Users\JaneDoe\Games; key sk-abc123DEF456ghi789 Bearer abcdefghijkl.mnop",
            @"C:\Users\JaneDoe", "JaneDoe");
        Assert.DoesNotContain("JaneDoe", s);
        Assert.DoesNotContain("abc123DEF456", s);
        Assert.DoesNotContain("abcdefghijkl", s);
        Assert.Contains("%USERPROFILE%", s);
    }
}
