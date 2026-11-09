using TruckingAdvisor.Core.Advisors;
using TruckingAdvisor.Core.Career;
using TruckingAdvisor.Core.Coaching;
using TruckingAdvisor.Core.Scoring;
using TruckingAdvisor.Core.Trips;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public sealed class DrivingAndScoringTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static AtsTelemetrySnapshot Live(Func<AtsTelemetrySnapshot, AtsTelemetrySnapshot>? f = null)
    {
        var s = new AtsTelemetrySnapshot { ConnectionState = ConnectionState.Connected, Timestamp = T0, EngineEnabled = true };
        return f is null ? s : f(s);
    }

    [Theory]
    [InlineData(65f, 65f, SpeedingSeverity.None)]
    [InlineData(68f, 65f, SpeedingSeverity.GentleAdvisory)]
    [InlineData(73f, 65f, SpeedingSeverity.ModerateWarning)]
    [InlineData(78f, 65f, SpeedingSeverity.SeriousWarning)]
    public void ClassifySpeeding_MatchesSpecificationTiers(float speed, float limit, SpeedingSeverity expected) =>
        Assert.Equal(expected, DrivingAnalyzer.ClassifySpeeding(speed - limit));

    [Fact]
    public void DriverScoringEngine_NormalizesByDistance_FairToLongHauls()
    {
        var longTrip = new DeliveryTripRecord
        {
            DistanceMiles = 1000f, AverageMpg = 6.5f, SpeedingIncidents = 2, HarshBrakingIncidents = 1,
            State = DeliveryState.Completed, DeliveredOnTime = true
        };
        var score = new DriverScoringEngine().CalculateTripScore(longTrip);
        Assert.True(score.FinalScore >= 95, $"Expected Legendary Trucker (95+), got {score.FinalScore}");
        Assert.Equal("Legendary Trucker", score.RatingLabel);
    }

    [Fact]
    public void Scoring_UnknownMpgIsNeutral_NotPunished()
    {
        var engine = new DriverScoringEngine();
        var unknown = engine.CalculateTripScore(new DeliveryTripRecord { DistanceMiles = 300, State = DeliveryState.Completed });
        Assert.Equal(85.0, unknown.FuelEfficiency);
    }

    [Fact]
    public void Analyzer_LowAirOnlyWhileMoving_AndOverRevUsesTruckMaxRpm()
    {
        var analyzer = new DrivingAnalyzer();
        var incidents = new List<DrivingIncident>();
        analyzer.IncidentDetected += (_, i) => incidents.Add(i);

        // Building air after engine start while parked: no alarm.
        analyzer.EvaluateSnapshot(Live(s => s with { AirPressurePsi = 40f, SpeedMph = 0f }), null);
        Assert.Empty(incidents);

        analyzer.EvaluateSnapshot(Live(s => s with { AirPressurePsi = 40f, SpeedMph = 30f }), null);
        Assert.Contains(incidents, i => i.Severity == "Critical" && i.AdvisoryMessage.Contains("air"));

        incidents.Clear();
        // 2000 RPM is fine on a 2500 RPM engine (92% = 2300)...
        analyzer.EvaluateSnapshot(Live(s => s with { EngineRpm = 2000f, MaxEngineRpm = 2500f, Timestamp = T0.AddMinutes(5) }), null);
        Assert.DoesNotContain(incidents, i => i.IncidentType == "OverRev");
        // ...but over-revving on a 2100 RPM engine.
        analyzer.EvaluateSnapshot(Live(s => s with { EngineRpm = 2000f, MaxEngineRpm = 2100f, Timestamp = T0.AddMinutes(6) }), null);
        Assert.Contains(incidents, i => i.IncidentType == "OverRev");
    }

    [Fact]
    public void Analyzer_CooldownUsesSnapshotClock()
    {
        var analyzer = new DrivingAnalyzer();
        int count = 0;
        analyzer.IncidentDetected += (_, _) => count++;
        var speeding = Live(s => s with { SpeedMph = 80f, SpeedLimitMph = 65f });
        analyzer.EvaluateSnapshot(speeding, null);
        analyzer.EvaluateSnapshot(speeding with { Timestamp = T0.AddSeconds(5) }, null);
        Assert.Equal(1, count);
        analyzer.EvaluateSnapshot(speeding with { Timestamp = T0.AddSeconds(60) }, null);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Advisor_SkipsAdviceWhoseInputsAreUnknown()
    {
        var advice = new FuelAndMaintenanceAdvisor().GenerateAdvisories(Live(), previousTripMpg: null, lastServiceOdometerMiles: null);
        Assert.Empty(advice); // no fuel capacity, no rest data, no service log -> nothing to say
    }

    [Fact]
    public void TripTracker_BuildsTripFromRealTelemetry()
    {
        var tracker = new TripTracker();
        DeliveryTripRecord? finished = null;
        tracker.TripFinished += (_, t) => finished = t;

        var start = Live(s => s with
        {
            JobActive = true, TrailerAttached = true, CargoName = "Lumber", OriginCity = "Phoenix", DestinationCity = "Denver",
            OdometerMiles = 1000f, FuelGallons = 200f, JobDeadlineGameMinutes = 5000, GameTimeMinutes = 1000, JobIncome = 18000
        });
        tracker.Update(start); // first snapshot already on a job -> mid-delivery
        Assert.True(tracker.ActiveTrip!.StartedMidDelivery);

        // Ten 1-second steps at 60 mph covering 10 miles total, burning 2 gallons.
        var s = start;
        for (int i = 1; i <= 10; i++)
        {
            s = s with { Timestamp = T0.AddSeconds(i), SpeedMph = 60f, OdometerMiles = 1000f + i, FuelGallons = 200f - 0.2f * i };
            tracker.Update(s);
        }
        Assert.Equal(DeliveryState.Underway, tracker.ActiveTrip!.State);

        // Plugin flips the delivered flag, clears onJob and the deadline in the same frame.
        tracker.Update(s with { Timestamp = T0.AddSeconds(11), SpeedMph = 0, JobDeliveredFlag = true, JobActive = false, JobDeadlineGameMinutes = 0, GameTimeMinutes = 4000 });

        Assert.NotNull(finished);
        Assert.Equal(DeliveryState.Completed, finished!.State);
        Assert.Equal(10f, finished.DistanceMiles, 2);
        Assert.Equal(2f, finished.FuelUsedGallons, 2);
        Assert.Equal(5f, finished.AverageMpg, 2);
        Assert.True(finished.DeliveredOnTime);
        Assert.Equal(18000ul, finished.RecordedRevenue);
        Assert.Null(tracker.ActiveTrip);
    }

    [Fact]
    public void TripTracker_IgnoresDemoData_AndDetectsCancel()
    {
        var tracker = new TripTracker();
        tracker.Update(new AtsTelemetrySnapshot { IsDemoData = true, ConnectionState = ConnectionState.SimulationFallback, JobActive = true });
        Assert.Null(tracker.ActiveTrip);

        DeliveryTripRecord? finished = null;
        tracker.TripFinished += (_, t) => finished = t;
        tracker.Update(Live(s => s with { JobActive = false }));
        tracker.Update(Live(s => s with { JobActive = true, Timestamp = T0.AddSeconds(1) }));
        Assert.False(tracker.ActiveTrip!.StartedMidDelivery);
        tracker.Update(Live(s => s with { JobActive = true, JobCancelledFlag = true, Timestamp = T0.AddSeconds(2) }));
        Assert.Equal(DeliveryState.Cancelled, finished!.State);
    }

    [Fact]
    public void TripTracker_TreatsFlagsAsToggles_SecondDeliveryIsNotACancel()
    {
        var tracker = new TripTracker();
        var results = new List<DeliveryState>();
        tracker.TripFinished += (_, t) => results.Add(t.State);
        var t = T0;
        AtsTelemetrySnapshot At(bool onJob, bool delivered) { t = t.AddSeconds(1); return Live(s => s with { Timestamp = t, JobActive = onJob, JobDeliveredFlag = delivered }); }

        tracker.Update(At(false, false));
        tracker.Update(At(true, false));
        tracker.Update(At(false, true));   // 1st delivery: flag false -> true
        tracker.Update(At(false, true));   // flag stays true afterwards: no new trip, no double count
        tracker.Update(At(true, true));    // next job starts with the flag still true
        tracker.Update(At(false, false));  // 2nd delivery: flag true -> false
        Assert.Equal(new[] { DeliveryState.Completed, DeliveryState.Completed }, results);
    }

    [Fact]
    public void TripTracker_WaitsForLateFlag_BeforeCallingItCancelled()
    {
        var tracker = new TripTracker();
        DeliveryTripRecord? finished = null;
        tracker.TripFinished += (_, x) => finished = x;
        tracker.Update(Live(s => s with { JobActive = false }));
        tracker.Update(Live(s => s with { JobActive = true, Timestamp = T0.AddSeconds(1) }));
        tracker.Update(Live(s => s with { JobActive = false, Timestamp = T0.AddSeconds(2) })); // job gone, no flag yet
        Assert.Null(finished);
        tracker.Update(Live(s => s with { JobActive = false, JobDeliveredFlag = true, Timestamp = T0.AddSeconds(3) }));
        Assert.Equal(DeliveryState.Completed, finished!.State);

        finished = null;
        tracker.Update(Live(s => s with { JobActive = true, JobDeliveredFlag = true, Timestamp = T0.AddSeconds(10) }));
        tracker.Update(Live(s => s with { JobActive = false, JobDeliveredFlag = true, Timestamp = T0.AddSeconds(11) }));
        tracker.Update(Live(s => s with { JobActive = false, JobDeliveredFlag = true, Timestamp = T0.AddSeconds(17) }));
        Assert.Equal(DeliveryState.Cancelled, finished!.State); // no flag within the grace period
    }

    [Fact]
    public void Defaults_HaveNoFabricatedHistory()
    {
        var company = new VirtualCompanyProfile();
        var career = new CareerProfile();
        Assert.Equal(0, company.TotalDeliveries);
        Assert.Equal(0, company.TotalMilesDriven);
        Assert.Equal(0m, company.RecordedRevenue);
        Assert.Empty(company.VisitedCities);
        Assert.Equal(1, career.Level);
        Assert.Equal(0, career.PersonalBestScore);

        var snapshot = new AtsTelemetrySnapshot();
        Assert.Equal(string.Empty, snapshot.CurrentState);
        Assert.Equal(string.Empty, snapshot.TruckMake);
        Assert.Equal(-1, snapshot.NextRestStopGameMinutes);
    }

    [Fact]
    public void CareerManager_OnlyCountsCompletedTrips()
    {
        var company = new VirtualCompanyProfile();
        var career = new CareerProfile();
        var trip = new DeliveryTripRecord { State = DeliveryState.Completed, DistanceMiles = 500, FuelUsedGallons = 80, Destination = "Denver", RecordedRevenue = 20000 };
        trip.ScoreBreakdown = new DriverScoringEngine().CalculateTripScore(trip);
        CareerManager.ApplyTrip(company, career, trip);
        CareerManager.ApplyTrip(company, career, new DeliveryTripRecord { State = DeliveryState.Cancelled, DistanceMiles = 100 });

        Assert.Equal(1, company.TotalDeliveries);
        Assert.Equal(500, company.TotalMilesDriven);
        Assert.Equal(6.25, company.LifetimeAverageMpg, 2);
        Assert.Equal(20000m, company.RecordedRevenue);
        Assert.Contains("Denver", company.VisitedCities);
        Assert.Equal(1, career.CurrentDamageFreeStreak);
    }
}
