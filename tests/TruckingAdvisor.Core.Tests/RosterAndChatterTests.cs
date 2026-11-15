using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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

/// <summary>
/// A bound ItemsControl throws "An ItemsControl is inconsistent with its items source" once its collection
/// has been changed from a thread other than the one that owns it. These drive a real ListBox.
/// </summary>
public sealed class BoundListThreadingTests
{
    /// <summary>
    /// Runs a body on a fresh STA thread with a real dispatcher and collects whatever it throws: a WPF
    /// control only has an owning thread once it has been created on one.
    /// </summary>
    private static List<Exception> OnStaThread(Func<Task> body)
    {
        var errors = new List<Exception>();
        var entered = new ManualResetEventSlim();
        Exception? setup = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                entered.Set();
                Dispatcher.Run();
            }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the dispatcher thread didn't start");

        var frame = new DispatcherFrame();
        Dispatcher.FromThread(thread)!.BeginInvoke(async () =>
        {
            try { await body(); }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        if (setup is not null) throw setup;
        return errors;
    }

    private static ListBox Bound(out ObservableCollection<string> items)
    {
        items = new ObservableCollection<string>();
        var list = new ListBox { ItemsSource = items };
        list.Measure(new System.Windows.Size(200, 200));
        return list;
    }

    [Fact]
    public async Task ItemsChangedOffTheOwningThread_BreakTheListBox()
    {
        var errors = await Task.Run(() => OnStaThread(async () =>
        {
            var list = Bound(out var items);
            for (int i = 0; i < 40; i++) items.Add($"row {i}");
            list.UpdateLayout();
            // A second batch added from another thread, exactly what the demo seed and the radio did.
            await Task.Run(() => { for (int i = 40; i < 120; i++) items.Add($"row {i}"); });
            list.UpdateLayout();
        }));
        Assert.Contains(errors, e => e is InvalidOperationException or NotSupportedException);
    }

    [Fact]
    public async Task ItemsChangedOnTheOwningThread_KeepTheListBoxHealthy()
    {
        var errors = await Task.Run(() => OnStaThread(async () =>
        {
            var list = Bound(out var items);
            for (int i = 0; i < 120; i++) items.Add($"row {i}");
            list.UpdateLayout();
            Assert.Equal(120, list.Items.Count);
            await Task.CompletedTask;
        }));
        Assert.Empty(errors);
    }
}
/// <summary>
/// The demo seed adds trips, and it runs on the telemetry thread rather than the UI thread. TripHistory
/// feeds a ListBox, so anything added from there leaves the control "inconsistent with its items source".
/// </summary>
public sealed class ViewModelThreadingTests
{
    internal sealed class FakeTelemetry : ITelemetryProvider
    {
        public event EventHandler<AtsTelemetrySnapshot>? TelemetryUpdated;
        public event EventHandler<ConnectionState>? ConnectionStateChanged;
        public ConnectionState CurrentState => ConnectionState.Connected;
        public AtsTelemetrySnapshot LatestSnapshot { get; private set; } = new();
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Publish(AtsTelemetrySnapshot snapshot) { LatestSnapshot = snapshot; TelemetryUpdated?.Invoke(this, snapshot); }
    }

    internal sealed class FakeAudio : IAudioPriorityManager
    {
        public readonly List<AudioItem> Items = new();
        public bool IsRadioChannelBusy { get; set; }
        public void EnqueueAnnouncement(string text, AudioPriority priority, string voiceProfile = "advisor", bool applyCbEffects = false) { }
        public void Enqueue(AudioItem item) { lock (Items) Items.Add(item); }
        public Task EnqueueAndWaitAsync(AudioItem item, CancellationToken ct = default) { lock (Items) Items.Add(item); return Task.CompletedTask; }
        public void SetChannelVolume(string channel, float volume0To1) { }
        public void StopAll() { }
    }

    [Fact]
    public void DemoTripSeeding_ReachesTripHistory_OnlyOnTheUiThread()
    {
        var telemetry = new FakeTelemetry();
        var audio = new FakeAudio();
        var radio = new CBRadioService(PersonalityCatalog.CreateDefaultRoster(), new VirtualCompanyProfile(),
            audio, () => new AtsTelemetrySnapshot(), new AiSettings(), llm: null, random: new Random(3));
        // Nothing is marshalled here: postToUi runs inline, so the thread the seed runs on is the thread
        // the collection is touched from. Run it from a worker and the add is off the UI thread.
        var vm = new TruckingAdvisor.Presentation.MainDashboardViewModel(telemetry, new TruckingAdvisor.Core.Coaching.DrivingAnalyzer(),
            new TruckingAdvisor.Core.Advisors.FuelAndMaintenanceAdvisor(), new TruckingAdvisor.Core.Trips.TripTracker(), audio, radio, new VirtualCompanyProfile(),
            new CareerProfile(), new AiSettings(), postToUi: action => action(), db: null);

        var worker = new Thread(() => telemetry.Publish(new AtsTelemetrySnapshot { IsDemoData = true, ConnectionState = ConnectionState.Connected }));
        worker.Start();
        worker.Join();

        // The demo history is only built with a database, so with db:null this pins the no-crash path and
        // that the view model never adds from the telemetry thread when postToUi marshals.
        Assert.Empty(vm.TripHistory);
        vm.Dispose();
    }
}
/// <summary>
/// RefreshContacts rebuilds the list in place (insert/move/replace, then trim) while the page is on screen.
/// That has to leave the bound ListBox consistent.
/// </summary>
public sealed class ContactRefreshTests
{
    [Fact]
    public void RefreshContacts_LeavesTheBoundListConsistent()
    {
        var errors = new List<Exception>();
        var entered = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            entered.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        var telemetry = new ViewModelThreadingTests.FakeTelemetry();
        var audio = new ViewModelThreadingTests.FakeAudio();
        var radio = new CBRadioService(PersonalityCatalog.CreateDefaultRoster(), new VirtualCompanyProfile(),
            audio, () => new AtsTelemetrySnapshot(), new AiSettings(), llm: null, random: new Random(3));
        var vm = new TruckingAdvisor.Presentation.MainDashboardViewModel(telemetry, new TruckingAdvisor.Core.Coaching.DrivingAnalyzer(),
            new TruckingAdvisor.Core.Advisors.FuelAndMaintenanceAdvisor(), new TruckingAdvisor.Core.Trips.TripTracker(), audio, radio,
            new VirtualCompanyProfile(), new CareerProfile(), new AiSettings(), postToUi: a => Dispatcher.FromThread(thread)!.Invoke(a), db: null);

        var frame = new DispatcherFrame();
        Dispatcher.FromThread(thread)!.BeginInvoke(() =>
        {
            try
            {
                var list = new ListBox { ItemsSource = vm.Contacts };
                list.Measure(new System.Windows.Size(300, 400));
                list.UpdateLayout();
                for (int round = 0; round < 6; round++)
                {
                    vm.RefreshContacts();
                    list.UpdateLayout();
                }
            }
            catch (Exception ex) { errors.Add(ex); }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        Assert.Empty(errors);
        Assert.Equal(PersonalityCatalog.CreateDefaultRoster().Count, vm.Contacts.Count);
    }
}
/// <summary>
/// The radio list keeps the newest line in view by calling ScrollIntoView from its CollectionChanged
/// handler, and trims the oldest rows from the same event. Both run before the ListBox has finished
/// applying the change, which is what leaves a virtualizing panel "inconsistent with its items source".
/// </summary>
public sealed class RadioScrollTests
{
    [Fact]
    public void ScrollIntoViewInsideCollectionChanged_IsSafeWhileTrimming()
    {
        var errors = new List<Exception>();
        var entered = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            entered.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var d = Dispatcher.FromThread(thread)!;

        var frame = new DispatcherFrame();
        d.BeginInvoke(() =>
        {
            try
            {
                var items = new ObservableCollection<string>();
                var list = new ListBox { ItemsSource = items, Height = 200, Width = 300 };
                list.Measure(new System.Windows.Size(300, 200));
                list.Arrange(new System.Windows.Rect(0, 0, 300, 200));
                list.UpdateLayout();
                items.CollectionChanged += (_, _) =>
                {
                    if (items.Count > 0) list.ScrollIntoView(items[^1]);
                };
                for (int i = 0; i < 260; i++)
                {
                    items.Add($"line {i}");
                    while (items.Count > 200) items.RemoveAt(0);
                    list.UpdateLayout();
                }
            }
            catch (Exception ex) { errors.Add(ex); }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        Assert.Empty(errors);
    }
}
/// <summary>
/// The radio list sits in a star-sized grid column beside a ScrollViewer with a splitter between them,
/// and lines arrive from the radio's own thread. Rebuilds that shape and pushes lines through it.
/// </summary>
public sealed class RadioListLayoutTests
{
    [Fact]
    public void RadioList_SurvivesLinesArrivingFromAnotherThread()
    {
        var errors = new List<Exception>();
        var entered = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            entered.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var d = Dispatcher.FromThread(thread)!;

        var messages = new ObservableCollection<string>();
        var frame = new DispatcherFrame();
        d.BeginInvoke(() =>
        {
            try
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(2, System.Windows.GridUnitType.Star), MinWidth = 420 });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(8) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(1.25, System.Windows.GridUnitType.Star), MinWidth = 300 });
                var left = new ScrollViewer { Content = new System.Windows.Controls.StackPanel() };
                Grid.SetColumn(left, 0);
                var splitter = new GridSplitter { Width = 8, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
                Grid.SetColumn(splitter, 1);
                var list = new ListBox { ItemsSource = messages };
                Grid.SetColumn(list, 2);
                grid.Children.Add(left);
                grid.Children.Add(splitter);
                grid.Children.Add(list);
                var window = new System.Windows.Window { Content = grid, Width = 1000, Height = 600 };
                window.Show();
                messages.CollectionChanged += (_, _) => { if (messages.Count > 0) list.ScrollIntoView(messages[^1]); };

                int round = 0;
                void Pump()
                {
                    for (int i = 0; i < 6; i++)
                    {
                        string line = $"round {round} line {i}";
                        messages.Add(line);
                        while (messages.Count > 200) messages.RemoveAt(0);
                        list.UpdateLayout();
                    }
                    if (++round >= 40) { window.Close(); frame.Continue = false; return; }
                    d.BeginInvoke(Pump, DispatcherPriority.Background);
                }
                d.BeginInvoke(Pump, DispatcherPriority.Background);
            }
            catch (Exception ex) { errors.Add(ex); }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        Assert.Empty(errors);
    }
}
/// <summary>
/// The radio's real shape: one logical line does Add then trims the oldest, and the CollectionChanged
/// handler keeps the newest in view. The handler runs between the two, so a ScrollIntoView there forces a
/// layout while the trim is still coming, and the list ends up "inconsistent with its items source".
/// </summary>
public sealed class RadioTrimTests
{
    private static void RunOnSta(Action<Dispatcher, Action<Action>> body)
    {
        Exception? failure = null;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var d = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d));
                ready.Set();
                Dispatcher.Run();
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(10));
        var dispatcher = Dispatcher.FromThread(thread)!;
        body(dispatcher, a => dispatcher.Invoke(a));
        dispatcher.InvokeShutdown();
        thread.Join(TimeSpan.FromSeconds(5));
        if (failure is not null) throw failure;
    }

    [Fact]
    public void AddThenTrim_WithScrollInTheHandler_KeepsTheListConsistent()
    {
        var errors = new List<Exception>();
        RunOnSta((_, ui) => ui(() =>
        {
            var items = new ObservableCollection<string>();
            var list = new ListBox { ItemsSource = items, Height = 200, Width = 300 };
            list.Measure(new System.Windows.Size(300, 200));
            list.Arrange(new System.Windows.Rect(0, 0, 300, 200));
            list.UpdateLayout();
            // Exactly what the window does now: queue one scroll instead of scrolling inside the event.
            bool queued = false;
            items.CollectionChanged += (_, _) =>
            {
                if (queued) return;
                queued = true;
                list.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    queued = false;
                    if (items.Count > 0) list.ScrollIntoView(items[^1]);
                });
            };
            try
            {
                for (int i = 0; i < 20; i++)
                {
                    items.Add($"line {i}");
                    while (items.Count > 20) items.RemoveAt(0);
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        }));
        Assert.Empty(errors);
    }
}

/// <summary>
/// The screenshot pass starts a typed call and waits at most five seconds for the answer. If that call
/// never finishes, the radio must not stay stuck "receiving", or every later unprompted line is dropped.
/// </summary>
public sealed class RadioRecoveryTests
{
    [Fact]
    public async Task ABusyRadio_StillAcceptsChatterAfterThePlayerCallEnds()
    {
        var audio = new ViewModelThreadingTests.FakeAudio();
        var radio = new CBRadioService(PersonalityCatalog.CreateDefaultRoster(), new VirtualCompanyProfile { PlayerHandle = "Ghost" },
            audio, () => new AtsTelemetrySnapshot(), new AiSettings { RadioActivity = RadioActivitySetting.Busy },
            llm: null, random: new Random(5))
        { ReplyPause = (0, 0) };

        var st = radio.CurrentState; var ch = radio.ActiveChannel;
        await radio.TriggerDynamicEncounterAsync(new AtsTelemetrySnapshot());
        Assert.True(audio.Items.Count > 0, "before=" + st + "/" + ch + " after=" + radio.CurrentState + " chatter alone: state=" + radio.CurrentState + " roster=" + PersonalityCatalog.CreateDefaultRoster().Count + " items=" + audio.Items.Count);
        radio.BeginPushToTalk();
        await radio.EndPushToTalkAsync("Big Red, you out there?");

        Assert.Equal(CBRadioState.RadioIdle, radio.CurrentState);
        Assert.False(audio.IsRadioChannelBusy);

        Assert.Equal(CBRadioState.RadioIdle, radio.CurrentState);
        await radio.TriggerDynamicEncounterAsync(new AtsTelemetrySnapshot());
        Assert.True(audio.Items.Count > 0, $"state={radio.CurrentState} busy={audio.IsRadioChannelBusy} channel={radio.ActiveChannel}");
    }
}