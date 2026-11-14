using TruckingAdvisor.AI;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

/// <summary>The expanded cast, their backstories, and truckers starting conversations on their own.</summary>
public sealed class RosterAndChatterTests
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
        public bool IsAvailable => true;
        public readonly List<(string System, string User)> Calls = new();
        public Task<LlmResult?> CompleteAsync(string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken ct)
        {
            Calls.Add((systemPrompt, userPrompt));
            return Task.FromResult<LlmResult?>(new LlmResult("Hell, that reminds me of a story.", new LlmUsage(10, 5)));
        }
    }

    [Fact]
    public void EveryRegular_HasABackstory_AndTheirOwnHandleAndTruck()
    {
        var roster = PersonalityCatalog.CreateDefaultRoster();
        Assert.True(roster.Count >= 12, "the roster should be a full cast");
        Assert.Equal(roster.Count, roster.Select(t => t.Handle).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(roster.Count, roster.Select(t => t.Id).Distinct().Count());
        Assert.All(roster, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Background), $"{t.Handle} needs a backstory");
            Assert.True(t.Background.Length >= 120, $"{t.Handle}'s backstory is too thin");
            Assert.False(string.IsNullOrWhiteSpace(t.SpeakingStyle));
            Assert.False(string.IsNullOrWhiteSpace(t.PersonalityTraits));
            Assert.False(string.IsNullOrWhiteSpace(t.Truck));
            Assert.NotEmpty(t.FavoriteTopics);
        });
    }

    [Fact]
    public void EveryRegular_CanStartAConversation_OnTheirOwnTopics_OrOffline()
    {
        Assert.All(PersonalityCatalog.CreateDefaultRoster(), t =>
        {
            Assert.NotEmpty(t.ChatterTopics);
            Assert.NotEmpty(t.OfflineOpeners);
            Assert.Contains(t.OfflineOpeners, o => o.Contains(t.Handle));
        });
    }

    [Theory]
    [InlineData(VoiceEngine.Local)]
    [InlineData(VoiceEngine.LocalKokoro)]
    public void EveryRegular_HasTheirOwnOfflineVoice_OnBothOnDeviceModels(VoiceEngine engine)
    {
        var speakers = PersonalityCatalog.CreateDefaultRoster()
            .Select(t => global::TruckingAdvisor.Audio.WindowsAudioSink.LocalSpeaker(t.VoiceId, engine)).ToList();
        // Supertonic only ships ten voices, so a few regulars share there; Kokoro has one each.
        Assert.True(speakers.Distinct().Count() >= (engine == VoiceEngine.LocalKokoro ? speakers.Count : 8));
    }

    [Fact]
    public void EveryVoiceInTheRoster_IsUnique()
    {
        var roster = PersonalityCatalog.CreateDefaultRoster();
        Assert.Equal(roster.Count, roster.Select(t => t.VoiceId).Distinct().Count());
        Assert.Equal(roster.Count, roster.Select(t => t.NeuralVoice).Distinct().Count());
    }

    [Fact]
    public void RegularsSpeakAtARelaxedPace_AndNewcomersToo()
    {
        static double Wpm(string spec) => 1 + double.Parse(spec.Split('|')[1].TrimEnd('%')) / 100;

        Assert.All(PersonalityCatalog.CreateDefaultRoster(), t =>
        {
            double pace = Wpm(PersonalityCatalog.NeuralVoiceFor(t));
            Assert.InRange(pace, 0.5, 0.65); // the service voices run fast; never above normal
        });
        Assert.All(PersonalityCatalog.NeuralVoicePool, spec => Assert.InRange(Wpm(spec), 0.5, 0.65));
    }

    [Fact]
    public void Newcomers_AreDistinct_AndKeepTheirOwnCharacter()
    {
        var a = PersonalityCatalog.GenerateDynamicTrucker(0);
        var b = PersonalityCatalog.GenerateDynamicTrucker(1);
        Assert.NotEqual(a.Id, b.Id);
        Assert.False(string.IsNullOrWhiteSpace(a.Background));
        Assert.NotEmpty(a.ChatterTopics);
        Assert.NotEmpty(a.OfflineOpeners);
        Assert.NotEqual(PersonalityCatalog.NeuralVoiceFor(a), PersonalityCatalog.NeuralVoiceFor(b));
    }

    [Fact]
    public void Prompt_GivesTheModelTheBackstory_AndPermissionToTalkLikeATrucker()
    {
        var trucker = PersonalityCatalog.CreateDefaultRoster().First(t => t.Handle == "Big Red");
        var prompt = ContextBuilder.BuildTruckerSystemPrompt(trucker, new VirtualCompanyProfile(), new AtsTelemetrySnapshot(),
            Array.Empty<RadioTurn>(), 19);
        Assert.Contains(trucker.Background, prompt);
        Assert.Contains("swearing", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("whatever you like", prompt);
    }

    [Fact]
    public void BanterPrompt_KnowsBothBackstories_AndTheTopic()
    {
        var roster = PersonalityCatalog.CreateDefaultRoster();
        var prompt = ContextBuilder.BuildBanterPrompt(roster[0], roster[1], new VirtualCompanyProfile(), new AtsTelemetrySnapshot(), 4,
            "the best truck stop coffee");
        Assert.Contains(roster[0].Background, prompt);
        Assert.Contains(roster[1].Background, prompt);
        Assert.Contains("the best truck stop coffee", prompt);
        Assert.Contains("swear", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnpromptedCall_CanBeAboutWhateverTheTruckerWants()
    {
        var audio = new RecordingAudio();
        var llm = new FakeLlm();
        var radio = new CBRadioService(PersonalityCatalog.CreateDefaultRoster(), new VirtualCompanyProfile { PlayerHandle = "Highway Ghost" },
            audio, () => new AtsTelemetrySnapshot(), new AiSettings { RadioActivity = RadioActivitySetting.Busy }, llm, random: new Random(7))
        { ReplyPause = (0, 0) };

        await radio.TriggerDynamicEncounterAsync(new AtsTelemetrySnapshot());

        var call = Assert.Single(llm.Calls);
        Assert.Contains("unprompted", call.User);
        Assert.Contains("your call", call.User);
        Assert.Contains("story", call.User);
        Assert.Single(audio.Items);
        Assert.Equal(AudioPriority.Low, audio.Items[0].Priority);
    }

    [Fact]
    public async Task UnpromptedCall_WithoutAi_UsesThatTruckersOwnOpener()
    {
        var audio = new RecordingAudio();
        var radio = new CBRadioService(PersonalityCatalog.CreateDefaultRoster(), new VirtualCompanyProfile { PlayerHandle = "Highway Ghost" },
            audio, () => new AtsTelemetrySnapshot(), new AiSettings { RadioActivity = RadioActivitySetting.Busy }, llm: null, random: new Random(7))
        { ReplyPause = (0, 0) };

        await radio.TriggerDynamicEncounterAsync(new AtsTelemetrySnapshot());

        var spoken = Assert.Single(audio.Items).Text;
        var speaker = radio.ActivePartner!;
        Assert.Contains(speaker.OfflineOpeners, o => o == spoken);
    }

    [Fact]
    public void PlayerCall_StillReachesTheTruckerByHandle()
    {
        var roster = PersonalityCatalog.CreateDefaultRoster();
        var dusty = roster.Single(t => t.Handle == "Dusty Dakota");
        var prompt = ContextBuilder.BuildTruckerSystemPrompt(dusty, new VirtualCompanyProfile { PlayerHandle = "Highway Ghost" },
            new AtsTelemetrySnapshot(), Array.Empty<RadioTurn>(), 19);
        Assert.Contains("Dusty Dakota", prompt);
        Assert.Contains(dusty.Truck, prompt);
        Assert.Contains(dusty.CurrentFictionalRegion, prompt);
    }


    [Fact]
    public void BothOnDeviceModels_DownloadFromTheirOwnRepository()
    {
        foreach (var file in global::TruckingAdvisor.Audio.Local.LocalVoiceModel.Files)
            Assert.Contains(global::TruckingAdvisor.Audio.Local.LocalVoiceModel.Repository, file.Url);
        foreach (var file in global::TruckingAdvisor.Audio.Local.KokoroVoiceModel.Files)
            Assert.Contains(global::TruckingAdvisor.Audio.Local.KokoroVoiceModel.Repository, file.Url);
        Assert.NotEqual(global::TruckingAdvisor.Audio.Local.LocalVoiceModel.Repository,
            global::TruckingAdvisor.Audio.Local.KokoroVoiceModel.Repository);
    }
}
