using System.Net;
using System.Text;
using System.Text.Json;
using TruckingAdvisor.AI;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public sealed class AiAndRadioTests
{
    private sealed class RecordingAudio : IAudioPriorityManager
    {
        public readonly List<AudioItem> Items = new();
        public bool IsRadioChannelBusy { get; set; }
        public void EnqueueAnnouncement(string text, AudioPriority priority, string voiceProfile = "advisor", bool applyCbEffects = false) =>
            Enqueue(new AudioItem(text, priority, voiceProfile, applyCbEffects));
        public void Enqueue(AudioItem item) { lock (Items) Items.Add(item); }
        public Task EnqueueAndWaitAsync(AudioItem item, CancellationToken ct = default) { Enqueue(item); return Task.CompletedTask; }
        public void SetChannelVolume(string channel, float volume0To1) { }
        public void StopAll() { }
    }

    private sealed class FakeLlm : ILlmClient
    {
        public bool IsAvailable { get; set; } = true;
        public string Reply = "Ten-four, driver.";
        public readonly List<(string System, string User)> Calls = new();
        public Task<LlmResult?> CompleteAsync(string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken ct)
        {
            Calls.Add((systemPrompt, userPrompt));
            return Task.FromResult<LlmResult?>(new LlmResult(Reply, new LlmUsage(10, 5)));
        }
    }

    private static CBRadioService Radio(RecordingAudio audio, ILlmClient? llm, List<TruckerPersonality>? roster = null, AtsTelemetrySnapshot? snap = null, AiSettings? settings = null) =>
        new(roster ?? PersonalityCatalog.CreateDefaultRoster(), new VirtualCompanyProfile { PlayerHandle = "Highway Ghost" },
            audio, () => snap ?? new AtsTelemetrySnapshot(), settings ?? new AiSettings(), llm, random: new Random(1)) { ReplyPause = (0, 0) };

    [Fact]
    public async Task Radio_WorksWithAiOff_UsingOfflineLines()
    {
        var audio = new RecordingAudio();
        var radio = Radio(audio, llm: null);
        var log = new List<string>();
        radio.TransmissionReceived += (_, e) => log.Add($"{e.Handle}: {e.Transcript}");

        radio.BeginPushToTalk();
        Assert.True(audio.IsRadioChannelBusy);
        await radio.EndPushToTalkAsync("Breaker one-nine, anybody got a copy?");

        Assert.Equal(2, log.Count);
        Assert.StartsWith("Highway Ghost:", log[0]);
        Assert.Single(audio.Items);
        Assert.True(audio.Items[0].ApplyCbEffects);
        Assert.Equal(CBRadioState.RadioIdle, radio.CurrentState);
        Assert.False(audio.IsRadioChannelBusy);
    }

    [Fact]
    public async Task Radio_UsesLlm_AndUpdatesRelationshipAndMemory()
    {
        var audio = new RecordingAudio();
        var llm = new FakeLlm { Reply = "Well, I hear you loud and clear, driver." };
        var roster = PersonalityCatalog.CreateDefaultRoster();
        var radio = Radio(audio, llm, roster);
        TruckerPersonality? updated = null;
        radio.TruckerUpdated += (_, t) => updated = t;

        radio.BeginPushToTalk();
        await radio.EndPushToTalkAsync("Big Red, you out there? Hauling a heavy load of lumber up toward Denver tonight.");

        Assert.Single(llm.Calls);
        Assert.Contains("Big Red", llm.Calls[0].System);
        Assert.Equal("Big Red", updated!.Handle);
        Assert.Equal(1, updated.ConversationCount);
        Assert.Single(updated.SummarizedMemories);
        Assert.Equal("Well, I hear you loud and clear, driver.", audio.Items.Single().Text);
    }

    [Fact]
    public async Task Banter_WithTwoTruckers_DoesNotCrash_AndUsesOneCall()
    {
        var audio = new RecordingAudio();
        var roster = PersonalityCatalog.CreateDefaultRoster().Take(2).ToList();
        var llm = new FakeLlm { Reply = "```json\n{\"lines\":[{\"handle\":\"Big Red\",\"text\":\"Anybody westbound?\"},{\"handle\":\"Highway Hank\",\"text\":\"Yeah, I'm rolling.\"}]}\n```" };
        var radio = Radio(audio, llm, roster);

        await radio.TriggerAiToAiBanterAsync(new AtsTelemetrySnapshot());

        Assert.Single(llm.Calls);
        Assert.Equal(2, audio.Items.Count);
        Assert.All(audio.Items, i => Assert.Equal(AudioPriority.Low, i.Priority));
    }

    private sealed class SlowLlm : ILlmClient
    {
        public bool IsAvailable => true;
        public TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<LlmResult?> CompleteAsync(string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Calls) == 1) await Gate.Task.WaitAsync(ct); // first call = chatter, hangs until cancelled
            return new LlmResult("Copy that, driver.", new LlmUsage(1, 1));
        }
    }

    [Fact]
    public async Task Player_CanInterruptChatter_AndChatterNeverStealsTheRadio()
    {
        var audio = new RecordingAudio();
        var llm = new SlowLlm();
        var radio = Radio(audio, llm);

        var chatter = radio.TriggerDynamicEncounterAsync(new AtsTelemetrySnapshot());
        await Task.Delay(50);
        Assert.Equal(CBRadioState.IncomingTransmission, radio.CurrentState);

        radio.BeginPushToTalk();                      // must be allowed mid-chatter
        Assert.Equal(CBRadioState.PlayerTransmitting, radio.CurrentState);
        await chatter;                                // chatter cancelled, must not reset state
        Assert.Equal(CBRadioState.PlayerTransmitting, radio.CurrentState);

        await radio.EndPushToTalkAsync("Anybody on here tonight?");
        Assert.Contains(audio.Items, i => i.Text == "Copy that, driver.");
        Assert.DoesNotContain(audio.Items, i => i.Priority == AudioPriority.Low); // the cancelled chatter never spoke
        Assert.Equal(CBRadioState.RadioIdle, radio.CurrentState);
    }

    /// <summary>Audio fake where playback lasts until StopAll (like a long voice line).</summary>
    private sealed class HoldingAudio : IAudioPriorityManager
    {
        private TaskCompletionSource _playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsRadioChannelBusy { get; set; }
        public int Started;
        public void EnqueueAnnouncement(string text, AudioPriority priority, string voiceProfile = "advisor", bool applyCbEffects = false) { }
        public void Enqueue(AudioItem item) { }
        public Task EnqueueAndWaitAsync(AudioItem item, CancellationToken ct = default) { Started++; return _playing.Task.WaitAsync(ct); }
        public void SetChannelVolume(string channel, float volume0To1) { }
        public void StopAll() { _playing.TrySetResult(); _playing = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    }

    [Fact]
    public async Task Reply_KeepsRadioBusyUntilVoiceEnds_AndInterruptingIsNotUndone()
    {
        var audio = new HoldingAudio();
        var radio = new CBRadioService(PersonalityCatalog.CreateDefaultRoster(), new VirtualCompanyProfile(), audio,
            () => new AtsTelemetrySnapshot(), new AiSettings(), llm: new FakeLlm()) { ReplyPause = (0, 0) };

        radio.BeginPushToTalk();
        var reply = radio.EndPushToTalkAsync("Anybody out there?");
        await Task.Delay(50);
        Assert.Equal(CBRadioState.PlayingAiResponse, radio.CurrentState); // voice still playing, not idle

        radio.BeginPushToTalk();          // interrupt the trucker mid-sentence
        await reply;                      // the old reply finishes because its audio was stopped
        Assert.Equal(CBRadioState.PlayerTransmitting, radio.CurrentState); // new transmission not reset
        Assert.True(audio.IsRadioChannelBusy);
    }

    [Fact]
    public void ParseBanter_RejectsGarbage() =>
        Assert.Null(CBRadioService.ParseBanter("sorry, I can't", PersonalityCatalog.CreateDefaultRoster()[0], PersonalityCatalog.CreateDefaultRoster()[1]));

    [Fact]
    public void Roster_StartsAsStrangers_WithNoInventedHistory()
    {
        var roster = PersonalityCatalog.CreateDefaultRoster();
        Assert.Equal(5, roster.Count);
        Assert.All(roster, t =>
        {
            Assert.Equal(RelationshipLevel.Stranger, t.Relationship);
            Assert.Empty(t.SummarizedMemories);
            Assert.Null(t.LastSpokenAt);
        });
        var validVoices = new[] { "alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse" };
        Assert.All(roster, t => Assert.Contains(t.VoiceId, validVoices));
    }

    [Fact]
    public void Relationship_RewardsQualityAndContinuity_NotSpam()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, RelationshipManager.ScoreExchange("ten-four", now, null));
        Assert.Equal(4, RelationshipManager.ScoreExchange(string.Join(' ', Enumerable.Repeat("word", 15)), now, null));
        Assert.Equal(1, RelationshipManager.ScoreExchange(string.Join(' ', Enumerable.Repeat("word", 15)), now, now.AddSeconds(-5)));
        Assert.Equal(5, RelationshipManager.ScoreExchange("how you been doing out there", now, now.AddDays(-2)));
        Assert.Equal(RelationshipLevel.FamiliarVoice, RelationshipManager.LevelFor(15));
        Assert.Equal(RelationshipLevel.OldFriend, RelationshipManager.LevelFor(500));
    }

    [Fact]
    public void ContextBuilder_OmitsUnknownValues()
    {
        var prompt = ContextBuilder.BuildTruckerSystemPrompt(PersonalityCatalog.CreateDefaultRoster()[0], new VirtualCompanyProfile(),
            new AtsTelemetrySnapshot(), Array.Empty<RadioTurn>(), 19);
        Assert.Contains("no game connection", prompt);
        Assert.DoesNotContain("fuelPercent", prompt);
        Assert.DoesNotContain("currentState", prompt);
        Assert.DoesNotContain("currentCity", prompt);
        Assert.DoesNotContain("speedMph", prompt); // no telemetry block without a game connection
    }

    // ---- OpenAI client against a fake HTTP server ----

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Response = """{"choices":[{"message":{"content":" Ten-four. "}}],"usage":{"prompt_tokens":100,"completion_tokens":20}}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task OpenAiClient_SendsModelAndKey_AndRecordsUsage()
    {
        var handler = new FakeHandler();
        var settings = new AiSettings { GlobalAiEnabled = true, ChatModel = "test-model", ChatInputPricePerMillionTokens = 1m, ChatOutputPricePerMillionTokens = 2m };
        var usage = new AiUsageTracker();
        var client = new OpenAiClientService(settings, usage, () => "sk-test", handler);

        var result = await client.CompleteAsync("sys", "hi", 100, CancellationToken.None);

        Assert.Equal("Ten-four.", result!.Text);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", handler.Last.Headers.Authorization!.ToString());
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(100, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _)); // not sent unless set
        Assert.Equal(1, usage.Data.Today.Requests);
        Assert.Equal(0.00014m, usage.Data.Today.EstimatedCostUsd);
    }

    [Fact]
    public async Task OpenAiClient_UnavailableWithoutKeyOrWhenDisabledOrOverLimit()
    {
        var handler = new FakeHandler();
        Assert.False(new OpenAiClientService(new AiSettings { GlobalAiEnabled = true }, new AiUsageTracker(), () => null, handler).IsAvailable);
        Assert.False(new OpenAiClientService(new AiSettings { GlobalAiEnabled = false }, new AiUsageTracker(), () => "k", handler).IsAvailable);

        var settings = new AiSettings { GlobalAiEnabled = true, DailyRequestLimit = 1 };
        var client = new OpenAiClientService(settings, new AiUsageTracker(), () => "k", handler);
        Assert.NotNull(await client.CompleteAsync("s", "u", 50, CancellationToken.None));
        Assert.False(client.IsAvailable);
        Assert.Null(await client.CompleteAsync("s", "u", 50, CancellationToken.None));
    }

    [Fact]
    public async Task OpenAiClient_ReportsAuthErrorsWithoutLeakingKey()
    {
        var handler = new FakeHandler { Status = HttpStatusCode.Unauthorized };
        var client = new OpenAiClientService(new AiSettings { GlobalAiEnabled = true }, new AiUsageTracker(), () => "sk-secret", handler);
        Assert.Null(await client.CompleteAsync("s", "u", 50, CancellationToken.None));
        Assert.Contains("401", client.LastError);
        Assert.DoesNotContain("sk-secret", client.LastError);
    }

    [Fact]
    public void UsageTracker_ResetsDailyTotals()
    {
        var day = new DateOnly(2026, 1, 1);
        var tracker = new AiUsageTracker(today: () => day);
        var s = new AiSettings { DailyRequestLimit = 1 };
        tracker.RecordChat(s, 1, 1);
        Assert.True(tracker.IsOverLimit(s));
        day = day.AddDays(1);
        Assert.False(tracker.IsOverLimit(s));
        Assert.Equal(1, tracker.Data.Lifetime.Requests);
    }

    [Fact]
    public void WavDuration_FromHeader()
    {
        var wav = new byte[44 + 32000];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(32000).CopyTo(wav, 28); // 16 kHz * 16-bit mono
        Assert.Equal(1.0, OpenAiClientService.WavDurationSeconds(wav), 3);
    }
}
