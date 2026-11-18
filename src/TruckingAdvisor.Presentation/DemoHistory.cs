using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Presentation;

/// <summary>
/// Demo mode has no game behind it, so the career, statistics and trips tabs would open empty and
/// showcase nothing. This fills them with a plausible recent history (never saved: demo data is not
/// recorded) so the screens can be looked at and exported.
/// </summary>
public static class DemoHistory
{
    private sealed record Route(string Origin, string Destination, string Cargo, double Miles, double Mpg, int Score, int Incidents, long Pay);

    private static readonly Route[] Routes =
    {
        new("Flagstaff, AZ", "Albuquerque, NM", "Bottled water", 322, 6.4, 91, 1, 1_840),
        new("Albuquerque, NM", "Amarillo, TX", "Building supplies", 289, 6.1, 84, 2, 1_610),
        new("Amarillo, TX", "Oklahoma City, OK", "Frozen goods", 259, 6.6, 95, 0, 1_470),
        new("Oklahoma City, OK", "Wichita, KS", "Auto parts", 161, 6.9, 88, 1, 940),
        new("Wichita, KS", "Kansas City, MO", "Palletised freight", 197, 6.2, 79, 3, 1_120),
        new("Kansas City, MO", "Des Moines, IA", "Household move", 194, 5.9, 86, 1, 1_260),
        new("Des Moines, IA", "Omaha, NE", "Grain", 136, 6.8, 93, 0, 830),
        new("Omaha, NE", "Sioux Falls, SD", "Livestock feed", 173, 6.3, 90, 1, 1_040),
        new("Sioux Falls, SD", "Fargo, ND", "Farm equipment", 244, 5.8, 82, 2, 1_390),
        new("Fargo, ND", "Billings, MT", "Steel coils", 447, 5.6, 88, 2, 2_480),
        new("Billings, MT", "Missoula, MT", "Lumber", 288, 6.0, 85, 3, 1_720),
        new("Missoula, MT", "Spokane, WA", "Paper rolls", 199, 6.5, 94, 0, 1_180)
    };

    /// <summary>Every component at the same value, so the weighted total lands on that score.</summary>
    private static TripScoreBreakdown Score(int score) => new()
    {
        SpeedCompliance = score, BrakingSmoothness = score, AccelerationSmoothness = score,
        FuelEfficiency = score, EngineManagement = score, TruckDamageScore = score,
        TrailerDamageScore = score, DeliveryCompletion = score, Punctuality = score, Consistency = score
    };

    /// <summary>Trips finished, oldest first, dated over the last few days.</summary>
    public static List<DeliveryTripRecord> BuildTrips(DateTimeOffset now)
    {
        var trips = new List<DeliveryTripRecord>();
        var time = now.AddDays(-6);
        foreach (var r in Routes)
        {
            double hours = r.Miles / 52.0;
            var start = time;
            time = time.AddHours(hours + 9);
            trips.Add(new DeliveryTripRecord
            {
                StartedAt = start,
                CompletedAt = start.AddHours(hours),
                State = DeliveryState.Completed,
                TruckMakeModel = "Peterbilt 389",
                Cargo = r.Cargo,
                CargoWeightLbs = 34_000,
                Origin = r.Origin,
                Destination = r.Destination,
                DistanceMiles = (float)r.Miles,
                PlannedDistanceMiles = (float)r.Miles,
                DrivingDuration = TimeSpan.FromHours(hours),
                AverageSpeedMph = 52,
                MaxSpeedMph = 68,
                AverageMpg = (float)r.Mpg,
                FuelUsedGallons = (float)(r.Miles / r.Mpg),
                SpeedingIncidents = r.Incidents,
                HarshBrakingIncidents = r.Incidents / 2,
                AddedTruckDamagePercent = r.Incidents > 2 ? 1.4f : 0,
                RecordedRevenue = (ulong)r.Pay,
                PaidRevenue = r.Pay,
                GameXp = r.Score * 3,
                ScoreBreakdown = Score(r.Score)
            });
        }
        return trips;
    }

    /// <summary>Company and career totals the demo trips would have produced.</summary>
    public static void Apply(List<DeliveryTripRecord> trips, VirtualCompanyProfile company, CareerProfile career)
    {
        company.TotalMilesDriven = trips.Sum(t => t.DistanceMiles);
        company.TotalDeliveries = trips.Count;
        company.DamageFreeDeliveries = trips.Count(t => t.AddedTruckDamagePercent <= 0);
        company.TotalFuelGallons = trips.Sum(t => t.FuelUsedGallons);
        company.RecordedRevenue = (decimal)trips.Sum(t => t.PaidRevenue);
        company.AverageDriverRating = trips.Average(t => t.ScoreBreakdown.FinalScore);
        foreach (var t in trips) company.VisitedCities.Add(t.Destination);

        career.Level = 6;
        career.CurrentXp = 4_210;
        career.NextLevelXp = 6_500;
        career.CurrentDamageFreeStreak = 3;
        career.BestDamageFreeStreak = 3;
        career.PersonalBestScore = trips.Max(t => t.ScoreBreakdown.FinalScore);
        career.BestTripMpg = trips.Max(t => t.AverageMpg);
        career.MilesSinceHarshBraking = 140;
    }
}
