using System.Net;
using TruckingAdvisor.Shared.Input;
using System.Text;
using System.Text.Json;
using TruckingAdvisor.AI;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public class ConversationTests
{
    private static List<TruckerPersonality> Two() => PersonalityCatalog.CreateDefaultRoster().Take(2).ToList();

    [Fact]
    public void Parse_KeepsTurnTaking_FixesSpeakers_AndStripsStageDirections()
    {
        var voices = Two(); // Big Red, Highway Hank
        string raw = "Sure! ```json\n{\"lines\":[" +
                     "{\"who\":\"Big Red\",\"text\":\"*laughs* Hank, you still running that W900?\"}," +
                     "{\"who\":\"Big Red\",\"text\":\"Highway Hank: Till the wheels fall off, Red.\"}," +
                     "{\"who\":\"Earl\",\"text\":\"(sighs) Them wheels are older than my coffee.\"}," +
                     "{\"who\":\"nobody\",\"text\":\"\\\"Ha! Ten-four on that.\\\"\"}]}\n``` hope that helps";
        var lines = TruckerConversation.Parse(raw, voices, "Big Red")!;
        Assert.Equal(new[] { "Big Red", "Highway Hank", "Big Red", "Highway Hank" }, lines.Select(l => l.Handle));
        Assert.Equal("Hank, you still running that W900?", lines[0].Text);
        Assert.Equal("Till the wheels fall off, Red.", lines[1].Text);
        Assert.Equal("Them wheels are older than my coffee.", lines[2].Text);
        Assert.Equal("Ha! Ten-four on that.", lines[3].Text);
        Assert.Null(TruckerConversation.Parse("no json here", voices, "Big Red"));
    }

    [Fact]
    public void Rating_ReachesEveryAiVoice()
    {
        var voices = Two();
        var company = new VirtualCompanyProfile { PlayerHandle = "Night Owl" };
        var snap = new AtsTelemetrySnapshot();
        string tv = TruckerConversation.System(voices, company, snap, Array.Empty<RadioTurn>(), 19, RadioRating.TvFriendly);
        string r = TruckerConversation.System(voices, company, snap, Array.Empty<RadioTurn>(), 19, RadioRating.RRated);
        Assert.Contains("no swearing at all", tv);
        Assert.Contains("swearing is natural and allowed", r);
        Assert.Contains("Never: slurs", r);
        Assert.Contains("swearing is natural", ContextBuilder.BuildTruckerSystemPrompt(voices[0], company, snap, Array.Empty<RadioTurn>(), 19,
            ConversationTimingMode.Natural, voices, RadioRating.RRated));
        Assert.Contains("no swearing at all", ContextBuilder.BuildTruckerSystemPrompt(voices[0], company, snap, Array.Empty<RadioTurn>(), 19));
        Assert.Contains("swear now and then", ContextBuilder.BuildDispatcherSystemPrompt(company, snap, Array.Empty<RadioTurn>(), RadioRating.RRated));
        Assert.Contains("clean, no swearing", ContextBuilder.BuildDispatcherSystemPrompt(company, snap, Array.Empty<RadioTurn>()));
        Assert.Equal(RadioRating.TvFriendly, new AiSettings().Rating);
    }

    private sealed class Audio : IAudioPriorityManager
    {
        public readonly List<AudioItem> Items = new();
        public Action<AudioItem>? OnPlay;
        public bool IsRadioChannelBusy { get; set; }
        public void EnqueueAnnouncement(string t, AudioPriority p, string v = "advisor", bool fx = false) => Enqueue(new AudioItem(t, p, v, fx));
        public void Enqueue(AudioItem item) { lock (Items) Items.Add(item); }
        public Task EnqueueAndWaitAsync(AudioItem item, CancellationToken ct = default) { Enqueue(item); OnPlay?.Invoke(item); return Task.CompletedTask; }
        public void SetChannelVolume(string c, float v) { }
        public void StopAll() { }
    }

    private sealed class JobLlm : IJobLlmClient
    {
        public readonly List<(LlmJob Job, string User)> Calls = new();
        public bool IsAvailable => true;
        public Task<LlmResult?> CompleteAsync(string s, string u, int m, CancellationToken ct) => CompleteAsync(s, u, m, LlmJob.Reply, ct);
        public Task<LlmResult?> CompleteAsync(string s, string u, int m, LlmJob job, CancellationToken ct)
        {
            lock (Calls) Calls.Add((job, u));
            string reply = job == LlmJob.Conversation
                ? "{\"lines\":[{\"who\":\"Big Red\",\"text\":\"Line " + Calls.Count + " from Red.\"},{\"who\":\"Highway Hank\",\"text\":\"Hank answers " + Calls.Count + ".\"}]}"
                : "{\"lines\":[{\"who\":\"Highway Hank\",\"text\":\"Ha, listen to him.\"},{\"who\":\"Big Red\",\"text\":\"Hush, Hank.\"}]}";
            return Task.FromResult<LlmResult?>(new LlmResult(reply, new LlmUsage(1, 1)));
        }
        public string LastUsed(LlmJob job) => "";
    }

    [Fact]
    public async Task Conversation_UsesTheConversationWriter_AndStopsTheMomentThePlayerKeysUp()
    {
        var audio = new Audio();
        var llm = new JobLlm();
        var radio = new CBRadioService(Two(), new VirtualCompanyProfile { PlayerHandle = "Night Owl" }, audio, () => new AtsTelemetrySnapshot(),
            new AiSettings { RadioActivity = RadioActivitySetting.VeryBusy }, llm, random: new Random(3)) { ReplyPause = (0, 0) };
        int played = 0;
        audio.OnPlay = _ => { if (++played == 3) radio.BeginPushToTalk(); };   // player keys up mid-conversation

        await radio.TriggerAiToAiBanterAsync(new AtsTelemetrySnapshot());

        Assert.All(llm.Calls, c => Assert.Equal(LlmJob.Conversation, c.Job));
        Assert.Equal(3, audio.Items.Count);                                  // nothing more after the key-up
        Assert.Equal(CBRadioState.PlayerTransmitting, radio.CurrentState);   // the player has the radio
        Assert.True(radio.ActivePartner is not null);                        // they'll answer the last voice
    }

    [Fact]
    public async Task VeryBusyRadio_RunsLongerConversations_ThatSignOff()
    {
        var audio = new Audio();
        var llm = new JobLlm();
        var radio = new CBRadioService(Two(), new VirtualCompanyProfile(), audio, () => new AtsTelemetrySnapshot(),
            new AiSettings { RadioActivity = RadioActivitySetting.VeryBusy }, llm, random: new Random(3)) { ReplyPause = (0, 0) };
        await radio.TriggerAiToAiBanterAsync(new AtsTelemetrySnapshot());
        Assert.Equal(5, llm.Calls.Count);
        Assert.Contains("sign off", llm.Calls[^1].User);
        Assert.Equal(10, audio.Items.Count);
    }

    [Fact]
    public async Task ALongConversation_IsNotCutOffByTheStuckRadioWatchdog()
    {
        var audio = new Audio();
        var llm = new JobLlm();
        var radio = new CBRadioService(Two(), new VirtualCompanyProfile(), audio, () => new AtsTelemetrySnapshot(),
            new AiSettings { RadioActivity = RadioActivitySetting.VeryBusy }, llm, random: new Random(3))
            { ReplyPause = (0, 0), CallWatchdog = TimeSpan.FromMilliseconds(250) };
        var notices = new List<string>();
        radio.Notice += (_, n) => { lock (notices) notices.Add(n); };
        audio.OnPlay = _ => Thread.Sleep(80);                    // 10 lines x 80 ms: far longer than the watchdog

        radio.BeginPushToTalk();                                 // a call arms the watchdog...
        await radio.EndPushToTalkAsync("");                    // (nothing said: back to idle)
        await radio.TriggerAiToAiBanterAsync(new AtsTelemetrySnapshot());   // ...then a long conversation starts

        Assert.Equal(10, audio.Items.Count);
        Assert.DoesNotContain(notices, n => n.Contains("back on the hook"));
        await Task.Delay(400);                                   // and once it's over the radio is simply idle
        Assert.Equal(CBRadioState.RadioIdle, radio.CurrentState);
    }

    // ---- Providers: conversations on DeepSeek, replies on OpenAI, each falling back to the other ----

    private sealed class FakeApi : HttpMessageHandler
    {
        public readonly List<(string Url, JsonElement Body)> Requests = new();
        public Func<string, string, HttpStatusCode>? Status;   // (url, model) -> status
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            string body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            var json = JsonDocument.Parse(body).RootElement.Clone();
            lock (Requests) Requests.Add((url, json));
            string model = json.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            var status = Status?.Invoke(url, model) ?? HttpStatusCode.OK;
            if (status != HttpStatusCode.OK)
                return new HttpResponseMessage(status) { Content = new StringContent("{\"error\":{\"message\":\"The model `" + model + "` does not exist\",\"code\":\"model_not_found\"}}") };
            string text = url.Contains("deepseek") ? $"deepseek:{model}" : $"openai:{model}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } }, usage = new { prompt_tokens = 5, completion_tokens = 5 } }), Encoding.UTF8, "application/json") };
        }
    }

    private static (LlmRouter Router, FakeApi Api, AiSettings Settings) Router(bool openAiKey = true, bool deepSeekKey = true)
    {
        var api = new FakeApi();
        var settings = new AiSettings { GlobalAiEnabled = true };
        var usage = new AiUsageTracker();
        var openAi = new OpenAiClientService(settings, usage, () => openAiKey ? "sk-openai" : null, api);
        var deepSeek = new DeepSeekClient(settings, usage, () => deepSeekKey ? "sk-deepseek" : null, api);
        return (new LlmRouter(openAi, deepSeek, settings), api, settings);
    }

    [Fact]
    public async Task Automatic_ConversationsGoToDeepSeek_RepliesToOpenAi()
    {
        var (router, api, settings) = Router();
        var reply = await router.CompleteAsync("s", "u", 80, LlmJob.Reply, CancellationToken.None);
        var convo = await router.CompleteAsync("s", "u", 80, LlmJob.Conversation, CancellationToken.None);
        Assert.StartsWith("openai:", reply!.Text);
        Assert.Equal("deepseek:deepseek-chat", convo!.Text);
        Assert.StartsWith("DeepSeek deepseek-chat", router.LastUsed(LlmJob.Conversation));
        Assert.Equal("DeepSeek deepseek-chat", settings.ConversationModel);

        // DeepSeek request: its own token parameter and a livelier temperature for conversation.
        var ds = api.Requests.Single(r => r.Url.Contains("deepseek")).Body;
        Assert.True(ds.TryGetProperty("max_tokens", out _));
        Assert.Equal(1.3, ds.GetProperty("temperature").GetDouble(), 2);
        // OpenAI conversation model choice is the strongest writer, not the quickest.
        settings.ConversationWriter = ConversationWriter.OpenAI;
        var viaOpenAi = await router.CompleteAsync("s", "u", 80, LlmJob.Conversation, CancellationToken.None);
        Assert.Equal("openai:gpt-4.1", viaOpenAi!.Text);
    }

    [Fact]
    public async Task OnlyOneKey_ThatProviderDoesEverything_AndAFailureFallsBack()
    {
        var (onlyOpenAi, _, _) = Router(deepSeekKey: false);
        Assert.StartsWith("openai:", (await onlyOpenAi.CompleteAsync("s", "u", 80, LlmJob.Conversation, CancellationToken.None))!.Text);

        var (onlyDeepSeek, _, _) = Router(openAiKey: false);
        Assert.StartsWith("deepseek:", (await onlyDeepSeek.CompleteAsync("s", "u", 80, LlmJob.Reply, CancellationToken.None))!.Text);

        var (router, api, _) = Router();
        api.Status = (url, model) => url.Contains("deepseek") ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
        Assert.StartsWith("openai:", (await router.CompleteAsync("s", "u", 80, LlmJob.Conversation, CancellationToken.None))!.Text);
    }

    [Fact]
    public async Task DeepSeek_SkipsARejectedModel_AndDiscoversNewerOnes()
    {
        var (router, api, _) = Router(openAiKey: false);
        api.Status = (url, model) => model == "deepseek-chat" ? HttpStatusCode.NotFound : HttpStatusCode.OK;
        var r = await router.CompleteAsync("s", "u", 80, LlmJob.Reply, CancellationToken.None);
        Assert.Equal("deepseek:deepseek-reasoner", r!.Text);

        var listed = new[] { ("deepseek-chat", 1L), ("deepseek-v5-pro", 3L), ("deepseek-v5-flash", 2L) };
        var forConvo = AiModelCatalog.Discover(listed, "deepseek-", AiModelCatalog.DeepSeekReply, LlmJobKind.Conversation);
        Assert.Equal(new[] { "deepseek-v5-pro", "deepseek-v5-flash" }, forConvo);
        var gpt = AiModelCatalog.Discover(new[] { ("gpt-7", 9L), ("gpt-7-pro", 9L), ("gpt-7-mini", 8L), ("gpt-7-realtime", 9L) }, "gpt-",
            AiModelCatalog.Conversation, LlmJobKind.Conversation);
        Assert.Equal(new[] { "gpt-7", "gpt-7-mini" }, gpt);               // no costly -pro, no realtime
    }
}

public class RatingToggleTests
{
    [Fact]
    public void RatingToggle_IsBindable()
    {
        var action = InputActions.Find("toggleRadioRating");
        Assert.NotNull(action);
        Assert.Contains("R-rated", action!.Label);
    }
}
