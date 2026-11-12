using TruckingAdvisor.AI;
using TruckingAdvisor.Core.Advisors;
using TruckingAdvisor.Core.Career;
using TruckingAdvisor.Core.Coaching;
using TruckingAdvisor.Core.Insights;
using TruckingAdvisor.Core.Scoring;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public sealed class SpecFeatureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- Voice commands & handle (spec 40D/40F) ----

    [Theory]
    [InlineData("TruckingAdvisor, switch to channel 19.", VoiceCommandKind.SwitchChannel, 19)]
    [InlineData("trucking advisor go to channel nineteen", VoiceCommandKind.SwitchChannel, 19)]
    [InlineData("Advisor, change to channel twenty-one", VoiceCommandKind.SwitchChannel, 21)]
    [InlineData("TruckingAdvisor, call dispatch.", VoiceCommandKind.CallDispatch, null)]
    [InlineData("TruckingAdvisor, what's my fuel range?", VoiceCommandKind.FuelReport, null)]
    [InlineData("TruckingAdvisor, how much farther to the destination?", VoiceCommandKind.DistanceReport, null)]
    [InlineData("TruckingAdvisor, turn down the radio.", VoiceCommandKind.VolumeDown, null)]
    [InlineData("TruckingAdvisor, mute", VoiceCommandKind.Mute, null)]
    [InlineData("TruckingAdvisor, do a barrel roll", VoiceCommandKind.Unknown, null)]
    [InlineData("Trucking Advisor? Repeat that", VoiceCommandKind.RepeatLast, null)]
    [InlineData("Advisor, mute", VoiceCommandKind.Mute, null)]
    [InlineData("Trucking adviser, switch to channel 1 9.", VoiceCommandKind.SwitchChannel, 19)]
    [InlineData("Truck advisor, call dispatch", VoiceCommandKind.CallDispatch, null)]
    [InlineData("trucking-advisor go to channel one nine", VoiceCommandKind.SwitchChannel, 19)]
    public void VoiceCommands_Parse(string text, VoiceCommandKind kind, int? channel)
    {
        var c = VoiceCommandParser.TryParse(text);
        Assert.NotNull(c);
        Assert.Equal(kind, c!.Kind);
        Assert.Equal(channel, c.Channel);
    }

    [Theory]
    [InlineData("Breaker one-nine, how's the fuel price at the next truck stop?")]
    [InlineData("Switch to channel 19 if you want to talk, driver")]
    [InlineData("My advisor told me to call dispatch")]
    [InlineData("Advisor says switch to channel 19")]
    [InlineData("TruckingAdvisor said to call dispatch, ha")]
    public void NormalRadioTalk_IsNeverACommand(string text) => Assert.Null(VoiceCommandParser.TryParse(text));

    [Theory]
    [InlineData("Breaker one-nine, call me Highway Ghost, over", "Highway Ghost")]
    [InlineData("my handle is road warrior", "Road Warrior")]
    [InlineData("Hey Big Red, they call me Night Owl.", "Night Owl")]
    [InlineData("Just hauling lumber to Denver", null)]
    [InlineData("Call me when you get to Denver", null)]
    [InlineData("call me back later, over", null)]
    public void HandleDetector_FindsIntroductions(string text, string? expected) =>
        Assert.Equal(expected, HandleDetector.TryDetect(text));

    // ---- Radio behavior ----

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

    private sealed class Llm : ILlmClient
    {
        public bool IsAvailable => true;
        public readonly List<(string System, string User)> Calls = new();
        public Task<LlmResult?> CompleteAsync(string s, string u, int max, CancellationToken ct)
        {
            Calls.Add((s, u));
            return Task.FromResult<LlmResult?>(new LlmResult("Ten-four.", new LlmUsage(1, 1)));
        }
    }

    private static CBRadioService Radio(Audio audio, ILlmClient? llm, AiSettings settings, VirtualCompanyProfile? company = null) =>
        new(PersonalityCatalog.CreateDefaultRoster(), company ?? new VirtualCompanyProfile { PlayerHandle = "Highway Ghost" },
            audio, () => new AtsTelemetrySnapshot(), settings, llm, random: new Random(2)) { ReplyPause = (0, 0) };

    [Fact]
    public async Task Commands_AreHandledByTheApp_NotSentToTruckers()
    {
        var audio = new Audio();
        var llm = new Llm();
        var radio = Radio(audio, llm, new AiSettings());
        VoiceCommand? got = null;
        radio.CommandReceived += (_, c) => got = c;

        radio.BeginPushToTalk();
        await radio.EndPushToTalkAsync("TruckingAdvisor, switch to channel 30");

        Assert.Equal(VoiceCommandKind.SwitchChannel, got!.Kind);
        Assert.Equal(30, radio.ActiveChannel);
        Assert.Empty(llm.Calls);
        Assert.Equal(CBRadioState.RadioIdle, radio.CurrentState);
    }

    [Fact]
    public async Task NewHandle_IsProposed_NotSavedSilently()
    {
        var company = new VirtualCompanyProfile { PlayerHandle = "Highway Ghost" };
        var radio = Radio(new Audio(), null, new AiSettings(), company);
        string? proposed = null;
        radio.HandleChangeProposed += (_, h) => proposed = h;
        radio.BeginPushToTalk();
        await radio.EndPushToTalkAsync("Breaker one-nine, call me Night Owl");
        Assert.Equal("Night Owl", proposed);
        Assert.Equal("Highway Ghost", company.PlayerHandle);
    }

    [Fact]
    public async Task QuietMode_StopsUnpromptedChatter_AndHourlyCapLimitsIt()
    {
        var quiet = new Audio();
        var radio = Radio(quiet, null, new AiSettings { ConversationMode = ConversationTimingMode.Quiet });
        await radio.TriggerDynamicEncounterAsync(new AtsTelemetrySnapshot());
        await radio.TriggerAiToAiBanterAsync(new AtsTelemetrySnapshot());
        Assert.Empty(quiet.Items);

        var capped = new Audio();
        var radio2 = Radio(capped, null, new AiSettings { MaxUnsolicitedPerHour = 2 });
        var now = T0;
        radio2.Clock = () => now;
        for (int i = 0; i < 5; i++) await radio2.TriggerDynamicEncounterAsync(new AtsTelemetrySnapshot());
        Assert.Equal(2, capped.Items.Count);
        now = T0.AddMinutes(61);
        await radio2.TriggerDynamicEncounterAsync(new AtsTelemetrySnapshot());
        Assert.Equal(3, capped.Items.Count);
    }

    [Fact]
    public async Task QuietPeriod_SilencesChatterAndRoutineDispatch_ButNotImportantCallsOrPlayer()
    {
        var audio = new Audio();
        var radio = Radio(audio, null, new AiSettings());
        var now = T0;
        radio.Clock = () => now;
        radio.QuietUntil = T0.AddMinutes(30);

        await radio.TriggerDynamicEncounterAsync(new AtsTelemetrySnapshot());
        await radio.TriggerAiToAiBanterAsync(new AtsTelemetrySnapshot());
        Assert.True(await radio.SendDispatchMessageAsync("Routine check-in.", important: false));
        Assert.Empty(audio.Items);

        Assert.True(await radio.SendDispatchMessageAsync("Your delivery window closes in 30 minutes.", important: true));
        Assert.Single(audio.Items);
        radio.BeginPushToTalk();
        await radio.EndPushToTalkAsync("Big Red, you out there?");
        Assert.True(audio.Items.Count >= 2); // the player still gets an answer

        int before = audio.Items.Count;
        now = T0.AddMinutes(31); // quiet period over
        Assert.False(radio.IsQuietPeriod);
        await radio.TriggerDynamicEncounterAsync(new AtsTelemetrySnapshot());
        Assert.True(audio.Items.Count > before);
    }

    [Fact]
    public async Task RadioReply_WorksWithLiveCargoContext()
    {
        var audio = new Audio();
        var snapshot = new AtsTelemetrySnapshot { ConnectionState = ConnectionState.Connected, JobActive = true, CargoWeightLbs = 40_250, SpeedMph = 62, SpeedLimitMph = 65 };
        var radio = new CBRadioService(PersonalityCatalog.CreateDefaultRoster(), new VirtualCompanyProfile(), audio, () => snapshot,
            new AiSettings()) { ReplyPause = (0, 0) };
        string? notice = null;
        radio.Notice += (_, n) => notice = n;
        radio.BeginPushToTalk();
        await radio.EndPushToTalkAsync("Big Red, you got your ears on?");
        Assert.Null(notice);
        Assert.NotEmpty(audio.Items);
    }

    [Theory]
    [InlineData(19, 1, 9)]
    [InlineData(30, 1, 19)]   // wraps forward
    [InlineData(19, -1, 30)]  // wraps backward
    [InlineData(17, -1, 9)]
    [InlineData(4, 1, 19)]    // from a non-preset channel, start at 19
    public void ChannelCycling_GoesBothWaysAndWraps(int from, int direction, int expected) =>
        Assert.Equal(expected, TruckingAdvisor.Presentation.MainDashboardViewModel.NextChannel(from, direction));

    [Fact]
    public async Task KnowledgeBoundary_TruckersNeverHearDispatchChannel()
    {
        var llm = new Llm();
        var radio = Radio(new Audio(), llm, new AiSettings());
        radio.SwitchChannel(30);
        radio.BeginPushToTalk();
        await radio.EndPushToTalkAsync("Dispatch, my secret pickup code is 4417");
        radio.SwitchChannel(19);
        radio.BeginPushToTalk();
        await radio.EndPushToTalkAsync("Big Red, you out there?");

        Assert.Contains("4417", llm.Calls[0].System + llm.Calls[0].User);   // dispatch saw it
        Assert.DoesNotContain("4417", llm.Calls[1].System);                  // trucker did not
    }

    [Fact]
    public async Task MemoriesOff_StoresNothing()
    {
        var roster = PersonalityCatalog.CreateDefaultRoster();
        var radio = new CBRadioService(roster, new VirtualCompanyProfile(), new Audio(), () => new AtsTelemetrySnapshot(),
            new AiSettings { RememberConversations = false }) { ReplyPause = (0, 0) };
        radio.BeginPushToTalk();
        await radio.EndPushToTalkAsync("Big Red, hauling steel to Reno tonight");
        Assert.All(roster, t => Assert.Empty(t.SummarizedMemories));
        Assert.Equal(1, roster.Single(t => t.Handle == "Big Red").ConversationCount);
    }

    [Fact]
    public async Task DispatchMessage_UsesCompanyChannel_EvenWhenOnNineteen()
    {
        var audio = new Audio();
        var radio = Radio(audio, null, new AiSettings(), new VirtualCompanyProfile { PlayerHandle = "Highway Ghost", DispatcherName = "Sarah" });
        var heard = new List<(string Handle, int Channel)>();
        radio.TransmissionReceived += (_, e) => heard.Add((e.Handle, e.Channel));
        Assert.True(await radio.SendDispatchMessageAsync("New load assigned: lumber to Denver.", important: false));
        Assert.Equal(("Dispatch", 30), heard.Single());
        Assert.StartsWith("Sarah (Dispatch) to Highway Ghost.", audio.Items.Single().Text);
        Assert.Equal("dispatcher", audio.Items.Single().Channel);
    }

    // ---- Dispatcher rules (spec 15) ----

    private static (DispatcherService D, List<string> Sent, Func<DateTimeOffset> Clock, Action<TimeSpan> Advance) Dispatcher(DispatchContactLevel level = DispatchContactLevel.Normal)
    {
        var sent = new List<string>();
        var now = T0;
        var d = new DispatcherService(new AiSettings { DispatchContact = level },
            (facts, important, ct) => { lock (sent) sent.Add(facts); return Task.FromResult(true); }, () => now);
        return (d, sent, () => now, ts => now += ts);
    }

    private static AtsTelemetrySnapshot Moving(Func<AtsTelemetrySnapshot, AtsTelemetrySnapshot>? f = null)
    {
        var s = new AtsTelemetrySnapshot { ConnectionState = ConnectionState.Connected, SpeedMph = 60, JobActive = true, DestinationCity = "Denver", NavigationDistanceRemainingMiles = 200 };
        return f is null ? s : f(s);
    }

    private static async Task Drain(List<string> sent, int expected)
    {
        for (int i = 0; i < 100; i++) { lock (sent) if (sent.Count >= expected) return; await Task.Delay(10); }
    }

    [Fact]
    public async Task Dispatcher_BriefsThenChecksInAndWarnsOnce()
    {
        var (d, sent, _, advance) = Dispatcher();
        var trip = new DeliveryTripRecord { Cargo = "Lumber", Origin = "Phoenix", Destination = "Denver", PlannedDistanceMiles = 426 };
        d.OnTripStarted(trip);
        d.Evaluate(Moving(), trip);
        await Drain(sent, 1);
        Assert.Contains("New load assigned: Lumber", sent[0]);

        advance(TimeSpan.FromMinutes(31));
        d.Evaluate(Moving(), trip);
        await Drain(sent, 2);
        Assert.Contains("Routine check-in", sent[1]);

        // Low fuel vs. distance: one notice, not a nag.
        for (int i = 0; i < 3; i++)
        {
            advance(TimeSpan.FromSeconds(1));
            d.Evaluate(Moving(s => s with { FuelGallons = 30, FuelCapacityGallons = 200, EstimatedRangeMiles = 150 }), trip);
            await Task.Delay(30);
        }
        await Drain(sent, 3);
        Assert.Single(sent, x => x.Contains("fuel stop"));
    }

    [Fact]
    public async Task Dispatcher_Off_IsSilent_AndMidDeliveryGetsNoBriefing()
    {
        var (off, sentOff, _, _) = Dispatcher(DispatchContactLevel.Off);
        var trip = new DeliveryTripRecord { Destination = "Denver" };
        off.OnTripStarted(trip);
        off.Evaluate(Moving(s => s with { NavigationDistanceRemainingMiles = 5 }), trip);
        await Task.Delay(100);
        Assert.Empty(sentOff);

        var (d, sent, _, _) = Dispatcher();
        d.OnTripStarted(new DeliveryTripRecord { Destination = "Denver", StartedMidDelivery = true });
        d.Evaluate(Moving(), trip);
        await Task.Delay(100);
        Assert.DoesNotContain(sent, x => x.Contains("New load"));
    }

    [Fact]
    public async Task Dispatcher_RetriesOneTimeNotice_IfRadioWasBusyUntilItExpired()
    {
        var sent = new List<string>();
        var now = T0;
        bool radioBusy = true;
        var d = new DispatcherService(new AiSettings(),
            (facts, important, ct) => { if (radioBusy) return Task.FromResult(false); lock (sent) sent.Add(facts); return Task.FromResult(true); },
            () => now);
        var trip = new DeliveryTripRecord { Destination = "Denver", StartedMidDelivery = true };
        d.OnTripStarted(trip);
        var near = Moving(s => s with { NavigationDistanceRemainingMiles = 10 });

        d.Evaluate(near, trip);          // queued, radio busy
        await Task.Delay(50);
        now = now.AddMinutes(4);         // expires while the player keeps talking
        d.Evaluate(near, trip);
        await Task.Delay(50);
        radioBusy = false;
        now = now.AddSeconds(1);
        d.Evaluate(near, trip);          // condition still true -> re-queued and delivered
        await Drain(sent, 1);
        Assert.Single(sent, x => x.Contains("miles from Denver"));
    }

    [Fact]
    public void GameTimeFormat() => Assert.Equal("Wed 14:30", DispatcherService.FormatGameTime(2 * 1440 + 14 * 60 + 30));

    // ---- Achievements, insights, co-driver ----

    [Fact]
    public void Achievements_UnlockFromRecordedData_Once()
    {
        var company = new VirtualCompanyProfile();
        var career = new CareerProfile();
        var trip = new DeliveryTripRecord { State = DeliveryState.Completed, DistanceMiles = 1050, FuelUsedGallons = 140, AverageMpg = 7.5f, Destination = "Denver" };
        trip.ScoreBreakdown = new DriverScoringEngine().CalculateTripScore(trip);
        CareerManager.ApplyTrip(company, career, trip);
        var unlocked = AchievementManager.Evaluate(company, career, trip, T0).Select(a => a.Id).ToList();

        Assert.Contains("first_haul", unlocked);
        Assert.Contains("long_haul_legend", unlocked);
        Assert.Contains("fuel_master", unlocked);
        Assert.Contains("perfect_delivery", unlocked);
        Assert.DoesNotContain("iron_trucker", unlocked);
        Assert.Empty(AchievementManager.Evaluate(company, career, trip, T0)); // no double unlock

        AchievementManager.AddMiles(career, 499);
        AchievementManager.OnHarshBraking(career);
        AchievementManager.AddMiles(career, 100);
        Assert.Equal(20, AchievementManager.List(company, career).Single(a => a.Id == "safe_driver").ProgressPercent);
        Assert.Empty(career.TruckEconomy); // trip had no truck name, so no per-truck entry
    }

    [Fact]
    public void Insights_NeedRealHistory()
    {
        var career = new CareerProfile();
        Assert.Empty(InsightEngine.Generate(Array.Empty<DeliveryTripRecord>(), career, "", T0));

        var trips = Enumerable.Range(0, 10).Select(i => new DeliveryTripRecord
        {
            State = DeliveryState.Completed,
            StartedAt = T0.AddDays(-i),
            DistanceMiles = 300,
            AverageMpg = i < 5 ? 7.0f : 6.0f, // newer trips are better
            Destination = i < 3 ? "Denver" : $"City {i}"
        }).ToList();
        var insights = InsightEngine.Generate(trips, career, "", T0.AddHours(1));
        Assert.Contains(insights, x => x.Contains("up from 6.0 MPG"));
        Assert.Contains(insights, x => x.Contains("3 deliveries to Denver"));
        Assert.Contains(insights, x => x.Contains("without damaging"));
    }

    [Fact]
    public void CoDriver_PersonalitiesDiffer_CriticalStaysPlain_MinimalStaysQuiet()
    {
        var speeding = new DrivingIncident { IncidentType = "Speeding", Severity = "Normal", Intensity = 8, AdvisoryMessage = "orig" };
        var critical = new DrivingIncident { IncidentType = "UnsafeOperation", Severity = "Critical", AdvisoryMessage = "Brake air pressure is critically low." };
        Assert.Null(CoDriverVoice.Phrase(speeding, CoDriverPersonality.Minimal));
        Assert.Contains("8", CoDriverVoice.Phrase(speeding, CoDriverPersonality.OldSchoolTrucker));
        Assert.NotEqual(CoDriverVoice.Phrase(speeding, CoDriverPersonality.Friendly), CoDriverVoice.Phrase(speeding, CoDriverPersonality.StrictInstructor));
        foreach (var p in Enum.GetValues<CoDriverPersonality>())
            Assert.Equal(critical.AdvisoryMessage, CoDriverVoice.Phrase(critical, p));
    }

    [Fact]
    public void Advisor_FlagsDeadlineRisk_WithoutSuggestingSpeeding()
    {
        var s = new AtsTelemetrySnapshot { JobActive = true, GameTimeMinutes = 1000, JobDeadlineGameMinutes = 1100, NavigationTimeRemainingMinutes = 220 };
        var advice = new FuelAndMaintenanceAdvisor().GenerateAdvisories(s, null, null);
        Assert.Contains(advice, a => a.Contains("past the deadline") && a.Contains("Don't speed"));
    }
}
