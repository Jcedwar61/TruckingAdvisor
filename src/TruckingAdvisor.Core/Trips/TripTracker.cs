using TruckingAdvisor.Core.Scoring;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Core.Trips;

/// <summary>
/// Follows the game's job flags and builds a <see cref="DeliveryTripRecord"/> from real telemetry:
/// distance from the odometer, fuel from tank level drops, time from snapshot timestamps,
/// damage as the increase since the trip started.
///
/// Lifecycle: JobStarted -> CargoAttached -> Underway (-> TrailerDisconnectedMidTrip) -> Completed / Cancelled.
/// If the app starts while a job is already running, the trip is marked StartedMidDelivery
/// and its stats only cover the part this app saw.
/// Demo snapshots are ignored so they never pollute career stats.
/// </summary>
public sealed class TripTracker
{
    private readonly DriverScoringEngine _scoring;
    private bool _seenFirstSnapshot;
    private AtsTelemetrySnapshot? _last;
    private float _baseTruckDamage, _baseTrailerDamage;
    private double _speedSum;
    private long _speedSamples;
    private bool _deliveredBaseline, _cancelledBaseline;
    private uint _deadline;
    private DateTimeOffset? _jobEndedAt;
    /// <summary>How long to wait for the delivered/cancelled flag after the job disappears.</summary>
    public static readonly TimeSpan EndGracePeriod = TimeSpan.FromSeconds(5);

    public DeliveryTripRecord? ActiveTrip { get; private set; }

    /// <summary>Raised when a trip finishes (completed or cancelled), with its final score.</summary>
    public event EventHandler<DeliveryTripRecord>? TripFinished;
    public event EventHandler<DeliveryState>? StateChanged;

    public TripTracker(DriverScoringEngine? scoring = null) => _scoring = scoring ?? new DriverScoringEngine();

    public void Update(AtsTelemetrySnapshot s)
    {
        if (s.IsDemoData || !s.IsLive)
        {
            _last = null; // don't bridge distance across a disconnect
            return;
        }

        bool firstSnapshot = !_seenFirstSnapshot;
        _seenFirstSnapshot = true;

        if (ActiveTrip is null)
        {
            if (s.JobActive) StartTrip(s, startedMidDelivery: firstSnapshot);
            _last = s;
            return;
        }

        Accumulate(s);
        if (s.JobDeadlineGameMinutes > 0) _deadline = s.JobDeadlineGameMinutes;

        // The plugin flips these flags on each event, so a change from the trip-start value is the event.
        if (s.JobDeliveredFlag != _deliveredBaseline) Finish(s, DeliveryState.Completed);
        else if (s.JobCancelledFlag != _cancelledBaseline) Finish(s, DeliveryState.Cancelled);
        else if (!s.JobActive)
        {
            // Job vanished without a flag yet; the event may land a moment later. If it never does
            // (profile change, quit to menu), record the trip as not completed.
            _jobEndedAt ??= s.Timestamp;
            if (s.Timestamp - _jobEndedAt.Value >= EndGracePeriod) Finish(s, DeliveryState.Cancelled);
        }
        else
        {
            _jobEndedAt = null;
            SetState(NextInProgressState(s));
        }

        _last = s;
    }

    /// <summary>Counts a coaching incident against the active trip.</summary>
    public void RecordIncident(DrivingIncident incident)
    {
        var t = ActiveTrip;
        if (t is null) return;
        incident.TripId = t.Id;
        switch (incident.IncidentType)
        {
            case "Speeding": t.SpeedingIncidents++; break;
            case "HarshBraking": t.HarshBrakingIncidents++; break;
            case "AggressiveAcceleration": t.AggressiveAccelerationIncidents++; break;
            case "OverRev": t.OverRevIncidents++; break;
        }
    }

    private void StartTrip(AtsTelemetrySnapshot s, bool startedMidDelivery)
    {
        _baseTruckDamage = s.OverallTruckDamagePercent;
        _baseTrailerDamage = MathF.Max(s.DamageTrailer, s.DamageCargo) * 100f;
        _speedSum = 0; _speedSamples = 0;
        _deliveredBaseline = s.JobDeliveredFlag;
        _cancelledBaseline = s.JobCancelledFlag;
        _deadline = s.JobDeadlineGameMinutes;
        _jobEndedAt = null;
        ActiveTrip = new DeliveryTripRecord
        {
            StartedAt = s.Timestamp,
            State = DeliveryState.JobStarted,
            StartedMidDelivery = startedMidDelivery,
            TruckMakeModel = $"{s.TruckMake} {s.TruckModel}".Trim(),
            Cargo = s.CargoName,
            CargoWeightLbs = s.CargoWeightLbs,
            Origin = s.OriginCity,
            Destination = s.DestinationCity,
            PlannedDistanceMiles = s.PlannedDistanceMiles,
            RecordedRevenue = s.JobIncome
        };
        StateChanged?.Invoke(this, DeliveryState.JobStarted);
        SetState(NextInProgressState(s));
    }

    private void Accumulate(AtsTelemetrySnapshot s)
    {
        var t = ActiveTrip!;
        if (_last is null) return;

        double dt = (s.Timestamp - _last.Timestamp).TotalSeconds;
        float dMiles = s.OdometerMiles - _last.OdometerMiles;
        // Ignore teleports, ferries/trains, and odometer resets.
        if (dMiles > 0 && dMiles < 5f) t.DistanceMiles += dMiles;

        float dFuel = _last.FuelGallons - s.FuelGallons;
        if (dFuel > 0 && dFuel < 5f) t.FuelUsedGallons += dFuel; // refuels (negative) are ignored

        if (!s.IsPaused && s.SpeedMph > 1f && dt is > 0 and < 2)
        {
            t.DrivingDuration += TimeSpan.FromSeconds(dt);
            _speedSum += s.SpeedMph;
            _speedSamples++;
        }
        t.MaxSpeedMph = MathF.Max(t.MaxSpeedMph, s.SpeedMph);
        t.AverageSpeedMph = _speedSamples > 0 ? (float)(_speedSum / _speedSamples) : 0f;
        t.AverageMpg = t.FuelUsedGallons > 0.05f ? t.DistanceMiles / t.FuelUsedGallons : 0f;
        t.AddedTruckDamagePercent = MathF.Max(0f, s.OverallTruckDamagePercent - _baseTruckDamage);
        t.AddedTrailerDamagePercent = MathF.Max(0f, MathF.Max(s.DamageTrailer, s.DamageCargo) * 100f - _baseTrailerDamage);
        if (string.IsNullOrEmpty(t.Cargo)) t.Cargo = s.CargoName;
        if (t.RecordedRevenue == 0) t.RecordedRevenue = s.JobIncome;
        t.ScoreBreakdown = _scoring.CalculateTripScore(t);
    }

    private DeliveryState NextInProgressState(AtsTelemetrySnapshot s)
    {
        var current = ActiveTrip!.State;
        if (!s.TrailerAttached)
            return current is DeliveryState.Underway or DeliveryState.TrailerDisconnectedMidTrip
                ? DeliveryState.TrailerDisconnectedMidTrip
                : DeliveryState.JobStarted;
        return s.SpeedMph > 3f || current is DeliveryState.Underway or DeliveryState.TrailerDisconnectedMidTrip
            ? DeliveryState.Underway
            : DeliveryState.CargoAttached;
    }

    private void Finish(AtsTelemetrySnapshot s, DeliveryState final)
    {
        var t = ActiveTrip!;
        t.State = final;
        t.CompletedAt = s.Timestamp;
        // The game may clear the deadline on delivery, so use the last one seen during the trip.
        if (final == DeliveryState.Completed && _deadline > 0 && s.GameTimeMinutes > 0)
            t.DeliveredOnTime = s.GameTimeMinutes <= _deadline;
        t.ScoreBreakdown = _scoring.CalculateTripScore(t);
        ActiveTrip = null;
        StateChanged?.Invoke(this, final);
        TripFinished?.Invoke(this, t);
    }

    private void SetState(DeliveryState next)
    {
        var t = ActiveTrip!;
        if (t.State == next) return;
        t.State = next;
        StateChanged?.Invoke(this, next);
    }
}
