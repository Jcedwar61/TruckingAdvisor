using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TruckingAdvisor.AI;
using TruckingAdvisor.AI.Streaming;
using TruckingAdvisor.Shared.Diagnostics;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

/// <summary>Fixes from the AI/spending audit: built-in prices, reservations, realtime, atomic files, key routing, log scrubbing.</summary>
public class AiAuditFixTests
{
    // ---- Fake HTTP: counts every request and answers chat, transcription, speech and model lists ----

    private sealed class CountingApi : HttpMessageHandler
    {
        public readonly List<string> Urls = new();
        public Func<string, HttpStatusCode>? Status;
        public int Count { get { lock (Urls) return Urls.Count; } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            lock (Urls) Urls.Add(url);
            var status = Status?.Invoke(url) ?? HttpStatusCode.OK;
            if (status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(status)
                    { Content = new StringContent("{\"error\":{\"message\":\"The model does not exist\",\"code\":\"model_not_found\"}}") });
            object body = url.EndsWith("/models") ? new { data = Array.Empty<object>() }
                : url.Contains("/audio/transcriptions") ? new { text = "ten-four" }
                : new { choices = new[] { new { message = new { content = "Ten-four." } } }, usage = new { prompt_tokens = 1000, completion_tokens = 1000 } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
        }
    }

    // ---- 1. Spend limit works with default settings: built-in, per-model, conservative prices ----

    [Fact]
    public void DefaultSettings_CountRealCost_SoTheSpendLimitCanTrip()
    {
        var s = new AiSettings();   // all prices 0, as in every existing settings file
        Assert.Equal(0m, s.ChatInputPricePerMillionTokens);
        var tracker = new AiUsageTracker();
        tracker.RecordChat(s, 1_000_000, 1_000_000, "gpt-5-mini");
        Assert.True(tracker.Data.Today.EstimatedCostUsd >= 2.25m);   // at least the list price (0.25 + 2.00)
        Assert.True(tracker.IsOverLimit(s));                          // default $1 limit now actually trips
    }

    // Official list prices checked 2026-11-27 (openai developers pricing page, api-docs.deepseek.com peak rates).
    [Theory]
    [InlineData("gpt-5-nano", 0.05, 0.40)]
    [InlineData("gpt-5-mini", 0.25, 2.00)]
    [InlineData("gpt-5", 1.25, 10.00)]
    [InlineData("gpt-5-chat-latest", 1.25, 10.00)]
    [InlineData("gpt-5.1", 1.25, 10.00)]
    [InlineData("gpt-5.2", 1.75, 14.00)]
    [InlineData("gpt-5.3-chat-latest", 1.75, 14.00)]
    [InlineData("gpt-5.4", 2.50, 15.00)]
    [InlineData("gpt-5.4-mini", 0.75, 4.50)]
    [InlineData("gpt-5.4-nano", 0.20, 1.25)]
    [InlineData("gpt-5.5", 5.00, 30.00)]
    [InlineData("gpt-5.6-sol", 5.00, 30.00)]     // launch price (promotional 4/20 ends 2026-11-30)
    [InlineData("gpt-5.6-terra", 2.00, 12.00)]
    [InlineData("gpt-5.6-luna", 0.20, 1.20)]
    [InlineData("gpt-6-astra", 10.00, 50.00)]
    [InlineData("gpt-6.2-astra", 8.00, 40.00)]
    [InlineData("gpt-6.2-sol", 2.00, 10.00)]
    [InlineData("gpt-6.1-sol", 2.00, 10.00)]
    [InlineData("gpt-6.1-terra", 1.00, 5.00)]
    [InlineData("gpt-6-luna", 0.10, 0.50)]
    [InlineData("gpt-6-cyber", 20.00, 100.00)]
    [InlineData("gpt-4.1", 2.00, 8.00)]
    [InlineData("gpt-4.1-mini", 0.40, 1.60)]
    [InlineData("gpt-4.1-nano", 0.10, 0.40)]
    [InlineData("gpt-4o", 2.50, 10.00)]
    [InlineData("gpt-4o-mini", 0.15, 0.60)]
    [InlineData("deepseek-flash", 0.30, 1.20)]
    [InlineData("deepseek-v4-flash", 0.30, 1.20)]
    [InlineData("deepseek-v4-pro", 1.32, 3.96)]
    [InlineData("deepseek-chat", 0.30, 1.20)]       // legacy names, retired 2026-07-24 (were billed as V4-Flash)
    [InlineData("deepseek-reasoner", 0.30, 1.20)]
    public void BuiltInPrices_ArePerModel_AtOrAboveListPricePlus20Percent(string model, double listIn, double listOut)
    {
        var p = AiModelCatalog.ChatPrice(model);
        Assert.True(p.Input >= (decimal)listIn * 1.2m, $"{model} input {p.Input} < list {listIn} + 20%");
        Assert.True(p.Output >= (decimal)listOut * 1.2m, $"{model} output {p.Output} < list {listOut} + 20%");
        // ...but not wildly above (a cap that trips far too early is no use either), except the legacy DeepSeek names.
        if (!model.StartsWith("deepseek-chat") && !model.StartsWith("deepseek-reasoner"))
            Assert.True(p.Output <= (decimal)listOut * 1.5m, $"{model} output {p.Output} far above list {listOut}");
    }

    [Fact]
    public void BuiltInPrices_UnknownModels_AreAtLeastTheDearestCurrentModelOfTheirTier()
    {
        var mini = AiModelCatalog.ChatPrice("gpt-5-mini");
        var full = AiModelCatalog.ChatPrice("gpt-5");
        Assert.True(full.Output > mini.Output);
        // Newer models the key lists: priced like the dearest current model of their size.
        Assert.True(AiModelCatalog.ChatPrice("gpt-7-turbo").Output >= 100m * 1.2m);   // gpt-6-cyber 20/100
        Assert.True(AiModelCatalog.ChatPrice("gpt-7-turbo").Input >= 20m * 1.2m);
        Assert.True(AiModelCatalog.ChatPrice("gpt-6.3-astra").Output >= 50m * 1.2m);  // gpt-6-astra 10/50
        Assert.True(AiModelCatalog.ChatPrice("gpt-6.3-sol").Output >= 30m * 1.2m);    // gpt-5.6-sol launch 5/30
        Assert.True(AiModelCatalog.ChatPrice("gpt-7-mini").Output >= 12m * 1.2m);     // gpt-5.6-terra 2/12
        Assert.True(AiModelCatalog.ChatPrice("gpt-6.2-terra").Output >= 12m * 1.2m);
        Assert.True(AiModelCatalog.ChatPrice("gpt-7-nano").Output >= 1.25m * 1.2m);   // gpt-5.4-nano 0.20/1.25
        Assert.True(AiModelCatalog.ChatPrice("gpt-6.2-luna").Input >= 0.20m * 1.2m);
        // A newer generation is never priced as the older one with the same leading digit.
        Assert.True(AiModelCatalog.ChatPrice("gpt-5.7").Output >= AiModelCatalog.ChatPrice("gpt-5.5").Output);
        Assert.True(AiModelCatalog.ChatPrice("o1").Output >= 60m * 1.2m);
        Assert.True(AiModelCatalog.DeepSeekPrice("deepseek-v5-ultra").Output >= 3.96m * 1.2m);

        Assert.True(AiModelCatalog.TranscriptionPricePerMinute("gpt-4o-transcribe") >= 0.006m * 1.2m);
        Assert.True(AiModelCatalog.TranscriptionPricePerMinute("gpt-4o-mini-transcribe") >= 0.003m * 1.2m);
        Assert.True(AiModelCatalog.TranscriptionPricePerMinute("whisper-1") >= 0.006m * 1.2m);
        Assert.True(AiModelCatalog.TranscriptionPricePerMinute("gpt-realtime-whisper") >= 0.017m * 1.2m);
        Assert.True(AiModelCatalog.TranscriptionPricePerMinute("some-new-stt") >= 0.017m * 1.2m);
        Assert.True(AiModelCatalog.SpeechPricePerMillionChars("tts-1") >= 15m * 1.2m);
        Assert.True(AiModelCatalog.SpeechPricePerMillionChars("tts-1-hd") >= 30m * 1.2m);

        foreach (var rt in new[] { "gpt-realtime", "gpt-realtime-1.5", "gpt-realtime-2", "gpt-realtime-2.1" })
        {
            var p = AiModelCatalog.RealtimePrice(rt);
            Assert.True(p.TextIn >= 4m * 1.2m && p.TextOut >= 24m * 1.2m && p.AudioIn >= 32m * 1.2m && p.AudioOut >= 64m * 1.2m && p.CachedIn >= 0.40m * 1.2m, rt);
        }
        foreach (var rt in new[] { "gpt-realtime-mini", "gpt-realtime-2.1-mini", "gpt-4o-mini-realtime-preview" })
        {
            var p = AiModelCatalog.RealtimePrice(rt);
            Assert.True(p.TextIn >= 0.60m * 1.2m && p.TextOut >= 2.40m * 1.2m && p.AudioIn >= 10m * 1.2m && p.AudioOut >= 20m * 1.2m && p.CachedIn >= 0.30m * 1.2m, rt);
        }
        var old = AiModelCatalog.RealtimePrice("gpt-4o-realtime-preview");
        Assert.True(old.TextIn >= 5m * 1.2m && old.AudioIn >= 40m * 1.2m && old.AudioOut >= 80m * 1.2m && old.CachedIn >= 2.50m * 1.2m);
    }

    [Theory]
    [InlineData(0.05, 0.06)]
    [InlineData(0.40, 0.50)]
    [InlineData(1.25, 1.50)]
    [InlineData(14.00, 17.00)]
    [InlineData(1.32, 1.60)]
    public void Headroom_IsListPlus20Percent_RoundedUp(double list, double expected) =>
        Assert.Equal((decimal)expected, AiModelCatalog.Headroom((decimal)list));

    [Fact]
    public void UserEnteredPrices_OverrideBuiltIns_ZeroMeansBuiltIn()
    {
        var s = new AiSettings { ChatInputPricePerMillionTokens = 1m, ChatOutputPricePerMillionTokens = 0m };
        var p = AiModelCatalog.EffectiveChatPrice(s, "gpt-5");
        Assert.Equal(1m, p.Input);
        Assert.Equal(AiModelCatalog.ChatPrice("gpt-5").Output, p.Output);
        s.DeepSeekOutputPricePerMillionTokens = 0.5m;
        Assert.Equal(0.5m, AiModelCatalog.EffectiveDeepSeekPrice(s, "deepseek-chat").Output);
    }

    [Fact]
    public async Task ChatCost_IsRecordedForTheModelThatAnswered()
    {
        var api = new CountingApi { Status = url => HttpStatusCode.OK };
        var s = new AiSettings { GlobalAiEnabled = true, Quality = AiQuality.Best, DailySpendLimitUsd = 0 };
        var usage = new AiUsageTracker();
        var client = new OpenAiClientService(s, usage, () => "sk-test", api);
        await client.CompleteAsync("sys", "hi", 80, CancellationToken.None);   // gpt-5 answers 1000 in / 1000 out
        var (pin, pout) = AiModelCatalog.ChatPrice("gpt-5");
        Assert.Equal((1000 * pin + 1000 * pout) / 1_000_000m, usage.Data.Today.EstimatedCostUsd);
    }

    // ---- 3. Reservations: checked and counted atomically, re-checked on every fallback attempt ----

    [Fact]
    public void TryReserve_IsAtomic_UnderConcurrency()
    {
        var s = new AiSettings { DailyRequestLimit = 5 };
        var tracker = new AiUsageTracker();
        int granted = 0;
        Parallel.For(0, 64, _ => { if (tracker.TryReserve(s) is { } r) { Interlocked.Increment(ref granted); r.Dispose(); } });
        Assert.Equal(5, granted);
        Assert.Equal(5, tracker.Data.Today.Requests);
        Assert.True(tracker.IsOverLimit(s));
    }

    [Fact]
    public void TryReserve_HoldsEstimatedSpend_UntilRecordedOrReleased()
    {
        var s = new AiSettings { DailySpendLimitUsd = 1m, DailyRequestLimit = 0 };
        var tracker = new AiUsageTracker();
        var first = tracker.TryReserve(s, 0.6m);
        Assert.NotNull(first);
        Assert.Null(tracker.TryReserve(s, 0.6m));          // would cross the cap while the first is in flight
        first!.Dispose();                                   // released without being billed
        var second = tracker.TryReserve(s, 0.6m);
        Assert.NotNull(second);
        tracker.RecordChatPriced(10, 10, 1m, 1m, second);   // actual cost replaces the hold; not counted twice
        Assert.Equal(2, tracker.Data.Today.Requests);
        Assert.Equal(0.00002m, tracker.Data.Today.EstimatedCostUsd);
        var third = tracker.TryReserve(s, 0.3m)!;
        third.CommitEstimate();                             // e.g. a timeout: the estimate is charged
        Assert.Equal(0.30002m, tracker.Data.Today.EstimatedCostUsd);
    }

    [Fact]
    public async Task FallbackLoop_ReChecksTheLimit_BeforeEveryAttempt()
    {
        var api = new CountingApi { Status = _ => HttpStatusCode.NotFound };   // every model "doesn't exist"
        var s = new AiSettings { GlobalAiEnabled = true, DailyRequestLimit = 2 };
        var client = new OpenAiClientService(s, new AiUsageTracker(), () => "sk-test", api);
        Assert.Null(await client.CompleteAsync("sys", "hi", 80, CancellationToken.None));
        Assert.Equal(2, api.Count);                   // stopped at the limit instead of walking the whole list
        Assert.Contains("limit", client.LastError);
    }

    [Fact]
    public async Task Transcription_AndVoices_GoThroughTheLimit()
    {
        var api = new CountingApi();
        var s = new AiSettings { GlobalAiEnabled = true, MicrophoneEnabled = true, VoiceEngine = VoiceEngine.OpenAI, DailyRequestLimit = 1 };
        var usage = new AiUsageTracker();
        var client = new OpenAiClientService(s, usage, () => "sk-test", api);
        var wav = WavAudio.FromPcm16Mono(new byte[32000], 16000);
        Assert.Equal("ten-four", await client.TranscribeAsync(wav, CancellationToken.None));
        Assert.True(usage.Data.Today.EstimatedCostUsd > 0);   // priced with the built-in per-minute estimate
        Assert.Null(await client.SynthesizeAsync("Ten-four", "onyx", CancellationToken.None));
        Assert.Equal(1, api.Count);
    }

    // ---- 2. Realtime: audio priced as audio, timeouts cancel and are charged, stalled connects time out ----

    private sealed class Transport : IRealtimeTransport
    {
        public readonly List<string> Sent = new();
        private readonly Queue<string?> _incoming;
        public bool StallConnect, StallReceive, Disposed;
        public Transport(params string?[] incoming) => _incoming = new Queue<string?>(incoming);
        public async Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct) { if (StallConnect) await Task.Delay(Timeout.Infinite, ct); }
        public Task SendAsync(string json, CancellationToken ct) { lock (Sent) Sent.Add(json); return Task.CompletedTask; }
        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            if (_incoming.Count > 0) return _incoming.Dequeue();
            if (StallReceive) await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public void RealtimeUsage_PricesAudioTokensAtAudioRates()
    {
        var usage = JsonNode.Parse("""
            {"input_tokens":1000,"output_tokens":1000,
             "input_token_details":{"text_tokens":200,"audio_tokens":800,"cached_tokens":100,"cached_tokens_details":{"text_tokens":100,"audio_tokens":0}},
             "output_token_details":{"text_tokens":100,"audio_tokens":900}}
            """);
        var t = RealtimeVoiceSession.ParseUsage(usage);
        Assert.Equal(new AiUsageTracker.RealtimeTokens(200, 800, 100, 0, 100, 900), t);
        var tracker = new AiUsageTracker();
        tracker.RecordRealtime("gpt-realtime-mini", t);
        var p = AiModelCatalog.RealtimePrice("gpt-realtime-mini");
        decimal expected = (100 * p.TextIn + 800 * p.AudioIn + 100 * p.CachedIn + 100 * p.TextOut + 900 * p.AudioOut) / 1_000_000m;
        Assert.Equal(expected, tracker.Data.Today.EstimatedCostUsd);
        Assert.True(expected > (1000 * p.TextIn + 1000 * p.TextOut) / 1_000_000m);   // far more than the old text-price estimate

        // No breakdown: everything counts as audio (never cheaper than the truth).
        Assert.Equal(new AiUsageTracker.RealtimeTokens(0, 50, 0, 0, 0, 20),
            RealtimeVoiceSession.ParseUsage(JsonNode.Parse("""{"input_tokens":50,"output_tokens":20}""")));
    }

    [Fact]
    public async Task RealtimeReply_Timeout_SendsResponseCancel_AndChargesTheEstimate()
    {
        var t = new Transport { StallReceive = true };
        var usage = new AiUsageTracker();
        await using var s = await RealtimeVoiceSession.StartAsync(t, new AiSettings(), usage, "sk-test", "be a trucker", "ash", default);
        s.StepTimeout = TimeSpan.FromMilliseconds(200);
        Assert.Null(await s.ReplyAsync(160, default));
        Assert.Contains("timed out", s.LastError);
        lock (t.Sent) Assert.Contains(t.Sent, m => m.Contains("\"response.cancel\""));
        Assert.True(usage.Data.Today.EstimatedCostUsd > 0);
        Assert.Equal(1, usage.Data.Today.Requests);
    }

    [Fact]
    public async Task RealtimeStart_StalledConnect_TimesOut_AndClosesTheTransport()
    {
        var t = new Transport { StallConnect = true };
        await Assert.ThrowsAsync<TimeoutException>(() =>
            RealtimeVoiceSession.StartAsync(t, new AiSettings(), new AiUsageTracker(), "k", "x", "ash", CancellationToken.None, TimeSpan.FromMilliseconds(200)));
        Assert.True(t.Disposed);
    }

    [Fact]
    public async Task RealtimeStart_RefusesInsecureAddresses()
    {
        var t = new Transport();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RealtimeVoiceSession.StartAsync(t, new AiSettings { ApiBaseUrl = "http://example.com/v1" }, new AiUsageTracker(), "k", "x", "ash", default));
        Assert.Equal("ws://localhost:8080/v1/realtime?model=gpt-realtime-mini",
            RealtimeVoiceSession.RealtimeUri(new AiSettings { ApiBaseUrl = "http://localhost:8080/v1" })!.ToString());
    }

    // ---- 4. Atomic files with a backup for the usage ledger ----

    [Fact]
    public void UsageLedger_SurvivesACorruptFile_ByLoadingTheBackup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ta-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "ai-usage.json");
            var day = new DateOnly(2026, 3, 1);
            var s = new AiSettings();
            var tracker = new AiUsageTracker(path, () => day);
            tracker.RecordChat(s, 100_000, 100_000, "gpt-5");
            tracker.RecordChat(s, 100_000, 100_000, "gpt-5");
            Assert.False(File.Exists(path + ".tmp"));
            Assert.True(File.Exists(path + ".bak"));
            File.WriteAllText(path, "{\"Day\":\"2026-03-01\",\"Today\":{\"Reque");   // torn write
            var reloaded = new AiUsageTracker(path, () => day);
            Assert.True(reloaded.Data.Today.Requests >= 1);
            Assert.True(reloaded.Data.Today.EstimatedCostUsd > 0);   // not silently reset to zero spend
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Settings_SaveAtomically_AndOldFilesWithZeroPricesStillLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ta-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "ai-settings.json");
            new AiSettings { DailySpendLimitUsd = 2.5m }.Save(path);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(2.5m, AiSettings.Load(path).DailySpendLimitUsd);
            File.WriteAllText(path, "{\"GlobalAiEnabled\":true,\"ChatInputPricePerMillionTokens\":0,\"DailySpendLimitUsd\":1.0}");
            var old = AiSettings.Load(path);
            Assert.True(old.GlobalAiEnabled);
            Assert.Equal(AiModelCatalog.ChatPrice("gpt-5-mini").Input, AiModelCatalog.EffectiveChatPrice(old, "gpt-5-mini").Input);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- 5. API key store: cached, invalidated on save/delete, IO errors don't escape ----

    [Fact]
    public void ApiKeyStore_CachesAndInvalidates_AndSwallowsIoErrors()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI only
        string dir = Path.Combine(Path.GetTempPath(), "ta-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            ApiKeyStore.Save("sk-first-key-123", AiLlmProvider.OpenAI, dir);
            Assert.Equal("sk-first-key-123", ApiKeyStore.Load(AiLlmProvider.OpenAI, dir));
            ApiKeyStore.Save("sk-second-key-456", AiLlmProvider.OpenAI, dir);
            Assert.Equal("sk-second-key-456", ApiKeyStore.Load(AiLlmProvider.OpenAI, dir));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            ApiKeyStore.Delete(AiLlmProvider.OpenAI, dir);
            Assert.Null(ApiKeyStore.Load(AiLlmProvider.OpenAI, dir));

            ApiKeyStore.Save("sk-third-key-789", AiLlmProvider.DeepSeek, dir);
            ApiKeyStore.Save("sk-third-key-789", AiLlmProvider.DeepSeek, dir);   // drops the cache
            string file = Directory.GetFiles(dir, "deepseek_key.dpapi").Single();
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Null(ApiKeyStore.Load(AiLlmProvider.DeepSeek, dir));      // locked: no key, no exception
            Assert.Equal("sk-third-key-789", ApiKeyStore.Load(AiLlmProvider.DeepSeek, dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- 6. Log scrubbing ----

    [Theory]
    [InlineData(@"{""path"":""C:\\Users\\JaneDoe\\AppData\\x.json""}")]          // JSON-escaped
    [InlineData("C:/Users/JaneDoe/AppData/x.json")]                               // forward slashes
    [InlineData("file:///C:/Users/JaneDoe/Documents/a.txt")]
    [InlineData("C%3A%5CUsers%5CJaneDoe%5CAppData")]                              // URL-encoded
    [InlineData("see Users%2FJaneDoe%2Fstuff")]
    [InlineData(@"D:\Users\JaneDoe\Games")]                                       // another drive
    [InlineData(@"E:\\Users\\JaneDoe\\Games")]
    public void Scrub_RemovesTheUserName_InEveryPathForm(string line)
    {
        string s = AppLog.Scrub(line, @"C:\Users\JaneDoe", "JaneDoe");
        Assert.DoesNotContain("JaneDoe", s);
    }

    [Fact]
    public void Scrub_AlsoCatchesOtherAccountsAndSpacedNames_ButKeepsPublicFolders()
    {
        Assert.DoesNotContain("Someone", AppLog.Scrub(@"C:\Users\Someone\x", @"C:\Users\JaneDoe", "JaneDoe"));
        Assert.DoesNotContain("Jane Doe", AppLog.Scrub(@"C:\Users\Jane Doe\x and C:/Users/Jane%20Doe/y", @"C:\Users\Jane Doe", "Jane Doe"));
        Assert.DoesNotContain("Jane", AppLog.Scrub("C%3A%5CUsers%5CJane%20Doe%5Cx", @"C:\Users\Jane Doe", "Jane Doe"));
        Assert.Contains(@"C:\Users\Public\Documents", AppLog.Scrub(@"C:\Users\Public\Documents", @"C:\Users\JaneDoe", "JaneDoe"));
        Assert.Contains("%USERPROFILE%", AppLog.Scrub("C:/Users/JaneDoe/x", @"C:\Users\JaneDoe", "JaneDoe"));
    }

    // ---- 7. Each key only goes to the host it was set up for, over https ----

    [Fact]
    public async Task CompatibleKey_IsNeverSentToTheOpenAiAddress()
    {
        var api = new CountingApi();
        var s = new AiSettings
        {
            GlobalAiEnabled = true, MicrophoneEnabled = true, VoiceEngine = VoiceEngine.OpenAI,
            Provider = AiLlmProvider.OpenAICompatible, CustomChatEndpoint = "https://llm.example.net/v1/chat/completions"
        };
        var client = new OpenAiClientService(s, new AiUsageTracker(), () => "sk-custom", api);
        Assert.NotNull(await client.CompleteAsync("s", "u", 50, CancellationToken.None));
        await client.RefreshModelsAsync();
        Assert.False(((ISpeechToText)client).IsAvailable);
        Assert.False(((ITextToSpeech)client).IsAvailable);
        Assert.Null(await client.TranscribeAsync(WavAudio.FromPcm16Mono(new byte[32000], 16000), CancellationToken.None));
        Assert.Null(await client.SynthesizeAsync("hi", "onyx", CancellationToken.None));
        Assert.All(api.Urls, u => Assert.StartsWith("https://llm.example.net/", u));
    }

    [Fact]
    public async Task DeepSeekKey_IsNeverSentToOpenAi_AndPlainHttpIsRefused()
    {
        var api = new CountingApi();
        var s = new AiSettings { GlobalAiEnabled = true, Provider = AiLlmProvider.DeepSeek };
        var openAi = new OpenAiClientService(s, new AiUsageTracker(), () => "sk-deepseek", api);
        Assert.False(openAi.IsAvailable);
        Assert.Null(await openAi.CompleteAsync("s", "u", 50, CancellationToken.None));
        Assert.Equal(0, api.Count);

        var insecure = new AiSettings { GlobalAiEnabled = true, ApiBaseUrl = "http://api.example.com/v1" };
        Assert.False(new OpenAiClientService(insecure, new AiUsageTracker(), () => "sk-x", api).IsAvailable);
        var ds = new DeepSeekClient(new AiSettings { GlobalAiEnabled = true, DeepSeekBaseUrl = "http://api.deepseek.com" }, new AiUsageTracker(), () => "sk-d", api);
        Assert.False(ds.IsAvailable);
        Assert.Equal(0, api.Count);

        // A local OpenAI-compatible server over http on this PC is fine.
        var local = new AiSettings { GlobalAiEnabled = true, Provider = AiLlmProvider.OpenAICompatible, ApiBaseUrl = "http://localhost:1234/v1" };
        var localClient = new OpenAiClientService(local, new AiUsageTracker(), () => "lm-studio", api);
        Assert.NotNull(await localClient.CompleteAsync("s", "u", 50, CancellationToken.None));
        Assert.Equal("http://localhost:1234/v1/chat/completions", api.Urls.Single());
    }

    // ---- 8. A prefetched exchange is cancelled when the conversation stops ----

    private sealed class Audio : IAudioPriorityManager
    {
        public Action<AudioItem>? OnPlay;
        public bool IsRadioChannelBusy { get; set; }
        public void EnqueueAnnouncement(string t, AudioPriority p, string v = "advisor", bool fx = false) { }
        public void Enqueue(AudioItem item) { }
        public Task EnqueueAndWaitAsync(AudioItem item, CancellationToken ct = default) { OnPlay?.Invoke(item); return Task.CompletedTask; }
        public void SetChannelVolume(string c, float v) { }
        public void StopAll() { }
    }

    private sealed class PrefetchLlm : IJobLlmClient
    {
        public int Calls;
        public readonly TaskCompletionSource SecondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource SecondCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsAvailable => true;
        public Task<LlmResult?> CompleteAsync(string s, string u, int m, CancellationToken ct) => CompleteAsync(s, u, m, LlmJob.Reply, ct);
        public async Task<LlmResult?> CompleteAsync(string s, string u, int m, LlmJob job, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Calls) == 1)
                return new LlmResult("{\"lines\":[{\"who\":\"Big Red\",\"text\":\"Morning.\"},{\"who\":\"Highway Hank\",\"text\":\"Morning, Red.\"}]}", new LlmUsage(1, 1));
            SecondStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { SecondCancelled.TrySetResult(); throw; }
            return null;
        }
        public string LastUsed(LlmJob job) => "";
    }

    [Fact]
    public async Task QuietModeMidConversation_CancelsThePrefetchedExchange()
    {
        var settings = new AiSettings { RadioActivity = RadioActivitySetting.VeryBusy };
        var llm = new PrefetchLlm();
        var audio = new Audio();
        var radio = new CBRadioService(PersonalityCatalog.CreateDefaultRoster().Take(2).ToList(), new VirtualCompanyProfile(), audio,
            () => new AtsTelemetrySnapshot(), settings, llm, random: new Random(3)) { ReplyPause = (0, 0) };
        audio.OnPlay = _ =>
        {
            settings.ConversationMode = ConversationTimingMode.Quiet;   // the player turns Quiet mode on
            llm.SecondStarted.Task.Wait(2000);                         // the next exchange is being written meanwhile
        };
        await radio.TriggerAiToAiBanterAsync(new AtsTelemetrySnapshot());
        var done = await Task.WhenAny(llm.SecondCancelled.Task, Task.Delay(3000));
        Assert.Same(llm.SecondCancelled.Task, done);
    }

    // ---- 9. Viewer names and messages stay out of the log ----

    [Fact]
    public void CallerLogLabel_HasNoNameOrMessage()
    {
        var label = CBRadioService.LogLabel(new CallerRequest("xX_SecretViewer_Xx", "my secret message", CallerRole.Trucker, CallerSource.Twitch));
        Assert.DoesNotContain("SecretViewer", label);
        Assert.DoesNotContain("secret message", label);
        Assert.Contains("Twitch", label);
        Assert.Contains("17", label);
    }
}
