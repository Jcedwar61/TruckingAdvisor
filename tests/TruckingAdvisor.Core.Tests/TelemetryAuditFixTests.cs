using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using TruckingAdvisor.AI;
using TruckingAdvisor.Audio;
using TruckingAdvisor.Core.Events;
using TruckingAdvisor.Core.Mapping;
using TruckingAdvisor.Core.Trips;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;
using TruckingAdvisor.Telemetry;
using Xunit;
using L = TruckingAdvisor.Telemetry.ScsTelemetryLayout;

namespace TruckingAdvisor.Core.Tests;

/// <summary>Disconnects reach consumers, game switches and restarts aren't events, and related thread-safety fixes.</summary>
public sealed class TelemetryAuditFixTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static AtsTelemetrySnapshot Live(double seconds, uint game = 2, ulong simMicros = 0) => new()
    {
        ConnectionState = ConnectionState.Connected,
        Timestamp = T0.AddSeconds(seconds),
        GameId = game,
        SimulationTimeMicros = simMicros == 0 ? (ulong)(1_000_000 + seconds * 1_000_000) : simMicros,
        EngineEnabled = true
    };

    /// <summary>What the provider publishes while the game is gone: time, state and the last game id.</summary>
    private static AtsTelemetrySnapshot NotLive(double seconds, uint game = 2) => new()
    {
        ConnectionState = ConnectionState.Disconnected,
        Timestamp = T0.AddSeconds(seconds),
        GameId = game
    };

    // ---- Trip tracker ----

    [Fact]
    public void GameRestartAfterAnEarlierDelivery_DoesNotCompleteTheTrip()
    {
        var tracker = new TripTracker();
        var finished = new List<DeliveryTripRecord>();
        tracker.TripFinished += (_, t) => finished.Add(t);

        // An earlier delivery this game session left the plugin's flag at true.
        tracker.Update(Live(0) with { JobDeliveredFlag = true });
        tracker.Update(Live(1) with { JobActive = true, JobDeliveredFlag = true, JobIncome = 5000, DeliveredRevenue = 9000 });
        // Game closed: the provider now publishes not-live snapshots.
        tracker.Update(NotLive(2));
        tracker.Update(NotLive(3));
        // Restarted two minutes later: a fresh plugin, flags back to false, clock starts again.
        tracker.Update(Live(120, simMicros: 5_000) with { JobActive = true, JobDeliveredFlag = false });
        tracker.Update(Live(121, simMicros: 6_000) with { JobActive = true, JobDeliveredFlag = false });

        Assert.Empty(finished);
        Assert.NotNull(tracker.ActiveTrip);
    }

    [Fact]
    public void DeliveryDuringAShortStall_IsStillCounted()
    {
        var tracker = new TripTracker();
        DeliveryTripRecord? finished = null;
        tracker.TripFinished += (_, t) => finished = t;

        tracker.Update(Live(0));
        tracker.Update(Live(1) with { JobActive = true });
        tracker.Update(NotLive(5)); // the game hitched for a few seconds
        // Same game session (clock moved forward, seconds later): the flag flip is a real delivery.
        tracker.Update(Live(6) with { JobActive = false, JobDeliveredFlag = true, DeliveredRevenue = 7000 });
        tracker.Update(Live(6.05) with { JobActive = false, JobDeliveredFlag = true, DeliveredRevenue = 7000 });

        Assert.Equal(DeliveryState.Completed, finished!.State);
        Assert.Equal(7000, finished.PaidRevenue);
    }

    [Fact]
    public void SwitchingGame_ClosesTheOldTripUnpaid_AndNeverCompletesIt()
    {
        var tracker = new TripTracker();
        var finished = new List<DeliveryTripRecord>();
        tracker.TripFinished += (_, t) => finished.Add(t);

        tracker.Update(Live(0, game: 2) with { JobDeliveredFlag = true });
        tracker.Update(Live(1, game: 2) with { JobActive = true, JobDeliveredFlag = true, CargoName = "Lumber" });
        // ETS2 now (no disconnect seen in between), with its own flags and its own job.
        tracker.Update(Live(2, game: 1) with { JobActive = true, JobDeliveredFlag = false, CargoName = "Milk" });
        tracker.Update(Live(3, game: 1) with { JobActive = true, JobDeliveredFlag = false, CargoName = "Milk" });

        var old = Assert.Single(finished);
        Assert.Equal(DeliveryState.Cancelled, old.State);
        Assert.Equal(0, old.PaidRevenue);
        Assert.Equal("Milk", tracker.ActiveTrip!.Cargo);
        Assert.Equal(1u, tracker.ActiveTrip.GameId);
        Assert.True(tracker.ActiveTrip.StartedMidDelivery);
    }

    [Fact]
    public void SameGameSession_NeedsSameGame_ForwardClock_AndAShortGap()
    {
        Assert.True(TripTracker.IsSameGameSession(Live(0), Live(5)));
        Assert.False(TripTracker.IsSameGameSession(Live(0), Live(5, game: 1)));
        Assert.False(TripTracker.IsSameGameSession(Live(10), Live(15, simMicros: 1_000)));    // clock went back: restarted
        Assert.False(TripTracker.IsSameGameSession(Live(0), Live(0) with { Timestamp = T0 + TripTracker.MaxStallGap }));
    }

    // ---- Game events ----

    [Fact]
    public void GameEvents_RestartOrGameSwitch_IsNotAFine()
    {
        var detector = new GameEventDetector();
        var events = new List<GameEvent>();
        detector.EventOccurred += (_, e) => events.Add(e);

        detector.Update(Live(0) with { FinedFlag = true, TollgateFlag = true });
        detector.Update(Live(1) with { FinedFlag = true, TollgateFlag = true });
        detector.Update(NotLive(2));
        detector.Update(Live(60, simMicros: 1_000));                  // restarted: flags zeroed
        detector.Update(Live(61, simMicros: 2_000) with { FineAmount = 500 });
        detector.Update(Live(62, game: 1) with { FinedFlag = true, TrainFlag = true }); // other game
        detector.Update(Live(63, game: 1) with { FinedFlag = true, TrainFlag = true, FineAmount = 500 });

        Assert.Empty(events);

        // A real fine afterwards is still reported.
        detector.Update(Live(64, game: 1) with { FinedFlag = false, TrainFlag = true });
        detector.Update(Live(65, game: 1) with { FinedFlag = false, TrainFlag = true, FineAmount = 250, FineOffence = "speeding" });
        var fine = Assert.Single(events);
        Assert.Equal(250, fine.Amount);
    }

    // ---- View model ----

    [Fact]
    public void ViewModel_SpeedChangeAcrossADisconnect_IsNotHarshAcceleration()
    {
        var telemetry = new ViewModelThreadingTests.FakeTelemetry();
        var audio = new ViewModelThreadingTests.FakeAudio();
        var radio = new CBRadioService(PersonalityCatalog.CreateDefaultRoster(), new VirtualCompanyProfile(),
            audio, () => new AtsTelemetrySnapshot(), new AiSettings { RadioActivity = RadioActivitySetting.Off }, llm: null, random: new Random(3));
        var vm = new TruckingAdvisor.Presentation.MainDashboardViewModel(telemetry, new TruckingAdvisor.Core.Coaching.DrivingAnalyzer(),
            new TruckingAdvisor.Core.Advisors.FuelAndMaintenanceAdvisor(), new TripTracker(), audio, radio, new VirtualCompanyProfile(),
            new CareerProfile(), new AiSettings { RadioActivity = RadioActivitySetting.Off }, postToUi: action => action(), db: null);

        var now = DateTimeOffset.UtcNow;
        telemetry.Publish(new AtsTelemetrySnapshot { ConnectionState = ConnectionState.Connected, Timestamp = now, GameId = 2, EngineEnabled = true });
        telemetry.Publish(new AtsTelemetrySnapshot { ConnectionState = ConnectionState.Disconnected, Timestamp = now.AddSeconds(0.5), GameId = 2 });
        // Back half a second later at highway speed with the throttle down: 130 mph/s against the empty snapshot.
        telemetry.Publish(new AtsTelemetrySnapshot
        {
            ConnectionState = ConnectionState.Connected, Timestamp = now.AddSeconds(1), GameId = 2,
            EngineEnabled = true, SpeedMph = 65, Throttle = 0.95f
        });

        Assert.DoesNotContain(vm.RecentIncidents, i => i.IncidentType == "AggressiveAcceleration");
        Assert.False(vm.CurrentTelemetry.IsDemoData);
    }

    // ---- Provider (needs Windows named shared memory, like the real plugin) ----

    private sealed class FakePlugin : IDisposable
    {
        public readonly string Name = $"Local\\TruckingAdvisorTest-{Guid.NewGuid():N}";
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly CancellationTokenSource _cts = new();
        private ulong _time = 1_000_000;
        private volatile bool _ticking;

        public FakePlugin(bool paused = false)
        {
            _mmf = MemoryMappedFile.CreateNew(Name, L.RequiredBytes);
            _view = _mmf.CreateViewAccessor();
            var b = new byte[L.RequiredBytes];
            b[L.SdkActive] = 1;
            b[L.Paused] = (byte)(paused ? 1 : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(L.PluginRevision), 12);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(L.GameId), 2);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(L.Time), _time);
            _view.WriteArray(0, b, 0, b.Length);
            _ticking = !paused;
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (_ticking) _view.Write(L.Time, _time += 20_000);
                    try { await Task.Delay(20, _cts.Token); } catch (OperationCanceledException) { }
                }
            });
        }

        /// <summary>The game went back to the menu or closed: the plugin says the SDK is inactive.</summary>
        public void Deactivate()
        {
            _ticking = false;
            _view.Write(L.SdkActive, (byte)0);
        }

        public void Dispose()
        {
            _cts.Cancel();
            Thread.Sleep(50);
            _view.Dispose();
            _mmf.Dispose();
        }
    }

    // Waits without holding a thread-pool thread: the provider and many other tests need the pool meanwhile.
    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task Provider_PublishesNotLive_WhenTheGameStops_ThenOnlyOnceASecond()
    {
        using var plugin = new FakePlugin();
        using var provider = new AtsMemoryMappedTelemetryProvider(plugin.Name);
        var seen = new ConcurrentQueue<AtsTelemetrySnapshot>();
        provider.TelemetryUpdated += (_, s) => seen.Enqueue(s);
        provider.Start();

        Assert.True(await WaitFor(() => seen.Any(s => s.IsLive), TimeSpan.FromSeconds(10)), "no live snapshot");
        plugin.Deactivate();
        Assert.True(await WaitFor(() => seen.LastOrDefault() is { IsLive: false }, TimeSpan.FromSeconds(10)), "no disconnect published");

        var notLive = seen.Last();
        Assert.Equal(ConnectionState.Disconnected, notLive.ConnectionState);
        Assert.Equal(2u, notLive.GameId);           // the game id is kept so units/maps don't flip
        Assert.False(notLive.IsDemoData);
        Assert.Equal(0f, notLive.SpeedMph);
        Assert.False(notLive.JobActive);

        int before = seen.Count(s => !s.IsLive);
        await Task.Delay(2500);
        int during = seen.Count(s => !s.IsLive) - before;
        Assert.InRange(during, 1, 4);                // throttled, not 20 a second
        Assert.Equal(ConnectionState.Disconnected, provider.CurrentState);
    }

    [Fact]
    public async Task Provider_PausedGameWhoseProcessIsGone_IsReportedDisconnected()
    {
        // No amtrucks/eurotrucks2 process runs during tests, so a paused, frozen block means the game is gone.
        using var plugin = new FakePlugin(paused: true);
        using var provider = new AtsMemoryMappedTelemetryProvider(plugin.Name);
        var seen = new ConcurrentQueue<AtsTelemetrySnapshot>();
        provider.TelemetryUpdated += (_, s) => seen.Enqueue(s);
        provider.Start();

        Assert.True(await WaitFor(() => seen.Any(s => s.ConnectionState == ConnectionState.GamePaused), TimeSpan.FromSeconds(10)), "never saw the paused game");
        Assert.True(await WaitFor(() => seen.LastOrDefault() is { IsLive: false }, TimeSpan.FromSeconds(15)), "paused forever");
        Assert.Contains("no longer running", provider.StatusDetail);
    }

    // ---- Microphone ----

    [Fact]
    public async Task Microphone_StopTimerOfThePreviousRecording_DoesNotCutOffTheNextOne()
    {
        using var mic = new MicrophoneRecorder { WithoutDevice = true };
        var block = new byte[3200];

        mic.Start();
        mic.Feed(block, block.Length);
        var first = mic.StopAsync();                 // its fallback timer fires about 1 s from now
        await Task.Delay(MicrophoneRecorder.Tail + TimeSpan.FromMilliseconds(100));
        mic.Feed(block, block.Length);               // past the tail: the first recording ends normally
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).Length > 44);

        mic.Start();                                 // the next transmission, within that second
        mic.Feed(block, block.Length);
        await Task.Delay(TimeSpan.FromMilliseconds(900)); // the first stop's timer has fired by now
        Assert.True(mic.IsRecording);

        var second = mic.StopAsync();
        Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(5))).Length > 44);
        Assert.False(mic.IsRecording);
    }

    // ---- Map memories ----

    [Fact]
    public async Task MapMemories_CanBeReadAndSavedWhileLearning()
    {
        var cities = new LocationMemory();
        var grades = new GradeMemory();
        string dir = Path.Combine(Path.GetTempPath(), $"ta-mem-{Guid.NewGuid():N}");
        var errors = new ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));

        var writer = Task.Run(() =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    cities.Learn($"City{i % 5000}", 1000 + i, 2000 + i);
                    grades.Record(i * 100.0, i * 7.0, -6f, 90f, 50f);
                    i++;
                }
                catch (Exception ex) { errors.Enqueue(ex); return; }
            }
        });
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    cities.Describe(1500, 2500);
                    grades.Ahead(0, 0, 90f);
                    cities.Save(Path.Combine(dir, "cities.json"));
                    grades.Save(Path.Combine(dir, "grades.json"));
                }
                catch (Exception ex) { errors.Enqueue(ex); return; }
            }
        });
        await Task.WhenAll(writer, reader);
        try
        {
            Assert.Empty(errors);
            Assert.NotEmpty(LocationMemory.Load(Path.Combine(dir, "cities.json")).Cities);
            Assert.NotEmpty(GradeMemory.Load(Path.Combine(dir, "grades.json")).Cells);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
