using System.IO.MemoryMappedFiles;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Telemetry;

/// <summary>
/// Reads ATS telemetry from the scs-sdk-plugin shared memory (<c>Local\SCSTelemetry</c>) on a
/// background task and raises <see cref="TelemetryUpdated"/> about 20 times per second.
///
/// - Live data only. Demo data is produced only when <c>enableDemoMode</c> is true, and every demo
///   snapshot is flagged with <see cref="AtsTelemetrySnapshot.IsDemoData"/>.
/// - Refuses to use the data if the plugin layout revision is not the one the offsets were built for.
/// - Treats the map as stale when the game clock stops advancing while not paused (game crashed).
/// - Events fire on a background thread; UI code must marshal to its own thread.
/// </summary>
public sealed class AtsMemoryMappedTelemetryProvider : ITelemetryProvider
{
    public const string DefaultMapName = ScsTelemetryLayout.MapName;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    private readonly string _mapName;
    private readonly bool _enableDemoMode;
    private readonly byte[] _buffer = new byte[ScsTelemetryLayout.RequiredBytes];
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private AtsTelemetrySnapshot? _previousSnapshot;
    private ulong _lastSimTime;
    private DateTimeOffset _lastSimTimeChange = DateTimeOffset.MinValue;
    private readonly DerivedTelemetryCalculator _derived = new();
    private DemoTelemetryGenerator? _demo;

    public event EventHandler<AtsTelemetrySnapshot>? TelemetryUpdated;
    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    public ConnectionState CurrentState { get; private set; } = ConnectionState.Disconnected;
    public AtsTelemetrySnapshot LatestSnapshot { get; private set; } = new();

    /// <summary>Human-readable reason for the current state (shown in the UI).</summary>
    public string StatusDetail { get; private set; } = "Not started";

    public AtsMemoryMappedTelemetryProvider(string mapName = DefaultMapName, bool enableDemoMode = false)
    {
        _mapName = mapName;
        _enableDemoMode = enableDemoMode;
    }

    public void Start()
    {
        if (_pollingTask is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        _pollingTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        CloseMap();
        UpdateState(ConnectionState.Disconnected, "Stopped");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        UpdateState(ConnectionState.Connecting, "Looking for the ATS telemetry plugin...");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (TryReadLive(out var live))
                {
                    Publish(_derived.Enrich(live, _previousSnapshot));
                }
                else if (_enableDemoMode)
                {
                    _demo ??= new DemoTelemetryGenerator();
                    var demo = _demo.Next(DateTimeOffset.UtcNow);
                    UpdateState(ConnectionState.SimulationFallback, "DEMO DATA - not from the game");
                    Publish(_derived.Enrich(demo, _previousSnapshot));
                }

                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                CloseMap();
                UpdateState(ConnectionState.Disconnected, $"Telemetry read error: {ex.Message}");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Publish(AtsTelemetrySnapshot snapshot)
    {
        _previousSnapshot = snapshot;
        LatestSnapshot = snapshot;
        var handlers = TelemetryUpdated;
        if (handlers is null) return;
        foreach (EventHandler<AtsTelemetrySnapshot> handler in handlers.GetInvocationList())
        {
            // One faulty subscriber must not stop telemetry for the others.
            try { handler(this, snapshot); } catch { /* subscriber bug; keep polling */ }
        }
    }

    private bool TryReadLive(out AtsTelemetrySnapshot snapshot)
    {
        snapshot = new AtsTelemetrySnapshot();
        if (!OperatingSystem.IsWindows())
        {
            UpdateState(ConnectionState.Disconnected, "Live telemetry needs Windows");
            return false;
        }

        if (_view is null && !TryOpenMap())
        {
            UpdateState(ConnectionState.Disconnected,
                "ATS not running, or the scs-sdk-plugin is not installed in bin\\win_x64\\plugins");
            return false;
        }

        // Copy the block, then confirm the game clock didn't change mid-copy (torn read).
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ulong before = _view!.ReadUInt64(ScsTelemetryLayout.Time);
            _view.ReadArray(0, _buffer, 0, _buffer.Length);
            ulong after = _view.ReadUInt64(ScsTelemetryLayout.Time);
            if (before == after) break;
        }

        var now = DateTimeOffset.UtcNow;
        switch (ScsTelemetryParser.TryParse(_buffer, now, out snapshot))
        {
            case ParseResult.Ok:
                break;
            case ParseResult.SdkInactive:
                UpdateState(ConnectionState.Disconnected, "Plugin loaded but the game isn't sending data (menu or closed)");
                return false;
            case ParseResult.UnsupportedRevision:
                UpdateState(ConnectionState.Disconnected,
                    $"Unsupported telemetry plugin revision {snapshot.PluginRevision} (need {ScsTelemetryLayout.SupportedRevision}, RenCloud V.1.12.x)");
                return false;
            default:
                UpdateState(ConnectionState.Disconnected, "Telemetry block too small");
                return false;
        }

        // Stale detection: clock frozen while the game says it's running = game crashed or hung.
        if (snapshot.SimulationTimeMicros != _lastSimTime || snapshot.IsPaused)
        {
            _lastSimTime = snapshot.SimulationTimeMicros;
            _lastSimTimeChange = now;
        }
        else if (now - _lastSimTimeChange > StaleAfter)
        {
            UpdateState(ConnectionState.Disconnected, "Telemetry stopped updating (game closed or frozen)");
            return false;
        }

        UpdateState(snapshot.ConnectionState, snapshot.IsPaused ? "Connected (game paused)" : "Connected to ATS");
        return true;
    }

    private bool TryOpenMap()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            _mmf = MemoryMappedFile.OpenExisting(_mapName, MemoryMappedFileRights.Read);
            _view = _mmf.CreateViewAccessor(0, ScsTelemetryLayout.RequiredBytes, MemoryMappedFileAccess.Read);
            return true;
        }
        catch (FileNotFoundException)
        {
            CloseMap();
            return false;
        }
    }

    private void CloseMap()
    {
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
    }

    private void UpdateState(ConnectionState newState, string detail)
    {
        StatusDetail = detail;
        if (CurrentState == newState) return;
        CurrentState = newState;
        ConnectionStateChanged?.Invoke(this, newState);
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}

/// <summary>
/// Values TruckingAdvisor calculates from consecutive snapshots. Marked "calculated" in the docs.
/// </summary>
public sealed class DerivedTelemetryCalculator
{
    private double _gradeAnchorX, _gradeAnchorY, _gradeAnchorZ;
    private bool _hasGradeAnchor;
    private float _mpgAnchorOdometer, _mpgAnchorFuel;
    private bool _hasMpgAnchor;
    private float _lastGrade, _lastInstantMpg;

    public AtsTelemetrySnapshot Enrich(AtsTelemetrySnapshot current, AtsTelemetrySnapshot? previous)
    {
        // Grade: rise over run once we've moved at least 25 m horizontally.
        if (!_hasGradeAnchor)
        {
            (_gradeAnchorX, _gradeAnchorY, _gradeAnchorZ, _hasGradeAnchor) = (current.WorldX, current.WorldY, current.WorldZ, true);
        }
        else
        {
            double dx = current.WorldX - _gradeAnchorX, dz = current.WorldZ - _gradeAnchorZ;
            double run = Math.Sqrt(dx * dx + dz * dz);
            if (run > 500) // teleport / ferry / new profile: reset
            {
                (_gradeAnchorX, _gradeAnchorY, _gradeAnchorZ) = (current.WorldX, current.WorldY, current.WorldZ);
                _lastGrade = 0f;
            }
            else if (run >= 25)
            {
                _lastGrade = (float)Math.Clamp((current.WorldY - _gradeAnchorY) / run * 100.0, -15.0, 15.0);
                (_gradeAnchorX, _gradeAnchorY, _gradeAnchorZ) = (current.WorldX, current.WorldY, current.WorldZ);
            }
        }

        // Instant MPG: fuel used over the last ~0.25 mi. Reset on refuel or odometer jump.
        if (!_hasMpgAnchor || current.FuelGallons > _mpgAnchorFuel + 0.05f || current.OdometerMiles < _mpgAnchorOdometer)
        {
            (_mpgAnchorOdometer, _mpgAnchorFuel, _hasMpgAnchor) = (current.OdometerMiles, current.FuelGallons, true);
        }
        else
        {
            float miles = current.OdometerMiles - _mpgAnchorOdometer;
            float gallons = _mpgAnchorFuel - current.FuelGallons;
            if (miles >= 0.25f)
            {
                _lastInstantMpg = gallons > 0.001f ? Math.Clamp(miles / gallons, 0f, 99f) : 0f;
                (_mpgAnchorOdometer, _mpgAnchorFuel) = (current.OdometerMiles, current.FuelGallons);
            }
        }

        float range = current.EstimatedRangeMiles;
        if (range <= 0 && current.HasFuelEconomy)
            range = current.FuelGallons * current.AverageMpg;

        return current with
        {
            CalculatedGradePercent = _lastGrade,
            InstantMpg = _lastInstantMpg,
            EstimatedRangeMiles = range
        };
    }
}

/// <summary>
/// Opt-in demo data for trying the app without the game. Always flagged as demo.
/// </summary>
public sealed class DemoTelemetryGenerator
{
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

    public AtsTelemetrySnapshot Next(DateTimeOffset now)
    {
        double seconds = (now - _start).TotalSeconds;
        float miles = (float)(seconds * 65.0 / 3600.0);
        float fuel = Math.Max(10f, 160f - miles / 6.3f);
        float remaining = Math.Max(0f, 426f - miles);
        return new AtsTelemetrySnapshot
        {
            Timestamp = now,
            ConnectionState = ConnectionState.SimulationFallback,
            IsDemoData = true,
            TruckMake = "Demo",
            TruckModel = "Truck",
            SpeedMph = 65f,
            SpeedLimitMph = 70f,
            EngineRpm = 1420f,
            MaxEngineRpm = 2100f,
            Gear = 16,
            DisplayedGear = 16,
            Throttle = 0.54f,
            EngineEnabled = true,
            ElectricEnabled = true,
            CruiseControlOn = true,
            CruiseControlSpeedMph = 65f,
            FuelGallons = fuel,
            FuelCapacityGallons = 240f,
            AverageMpg = 6.3f,
            WaterTempFahrenheit = 192f,
            OilPressurePsi = 46f,
            AirPressurePsi = 118f,
            TrailerAttached = true,
            CargoName = "Demo Cargo",
            CargoWeightLbs = 40000f,
            JobActive = true,
            OriginCity = "Phoenix",
            DestinationCity = "Denver",
            PlannedDistanceMiles = 426f,
            NavigationDistanceRemainingMiles = remaining,
            NavigationTimeRemainingMinutes = remaining / 59f * 60f,
            OdometerMiles = 1000f + miles,
            WorldX = seconds * 29.0,
            GameTimeMinutes = 23 * 60 + 45 + (uint)(seconds / 3.0),
            TimeScale = 19f,
            NextRestStopGameMinutes = 310
        };
    }
}
