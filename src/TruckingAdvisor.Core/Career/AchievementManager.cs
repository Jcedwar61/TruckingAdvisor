using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Core.Career;

/// <summary>
/// Driver achievements and company milestones, computed only from trips this app recorded.
/// Separate from ATS's own achievements and economy.
/// </summary>
public static class AchievementManager
{
    private sealed record Definition(string Id, string Title, string Description, string Icon, string Category,
        Func<VirtualCompanyProfile, CareerProfile, DeliveryTripRecord?, double> Progress);

    private static double Ratio(double value, double target) => Math.Clamp(value / target, 0, 1);

    private static readonly Definition[] Definitions =
    {
        new("first_haul", "First Haul", "Complete your first delivery.", "🏁", "Driver",
            (c, _, _) => Ratio(c.TotalDeliveries, 1)),
        new("long_haul_legend", "Long Haul Legend", "Complete a delivery of 1,000+ miles (as recorded by TruckingAdvisor).", "🛣️", "Driver",
            (_, _, t) => t is { State: DeliveryState.Completed } ? Ratio(t.DistanceMiles, 1000) : 0),
        new("fuel_master", "Fuel Master", "Complete a delivery of 50+ miles averaging more than 7 MPG.", "⛽", "Driver",
            (_, _, t) => t is { State: DeliveryState.Completed, DistanceMiles: >= 50 } && t.AverageMpg > 7 ? 1 : 0),
        new("safe_driver", "Safe Driver", "Drive 500 miles without a harsh braking event.", "🛡️", "Driver",
            (_, k, _) => Ratio(k.MilesSinceHarshBraking, 500)),
        new("perfect_delivery", "Perfect Delivery", "Complete a delivery with no added truck, trailer or cargo damage.", "📦", "Driver",
            (_, _, t) => t is { State: DeliveryState.Completed, AddedTruckDamagePercent: < 0.05f, AddedTrailerDamagePercent: < 0.05f } ? 1 : 0),
        new("interstate_explorer", "Interstate Explorer", "Deliver to 10 different cities (the game doesn't report states).", "🗺️", "Driver",
            (c, _, _) => Ratio(c.VisitedCities.Count, 10)),
        new("million_dollar_trucker", "Million Dollar Trucker", "Earn 1,000,000 in recorded in-game delivery income.", "💰", "Driver",
            (c, _, _) => Ratio((double)c.RecordedRevenue, 1_000_000)),
        new("iron_trucker", "Iron Trucker", "Complete 10 damage-free deliveries in a row.", "🔩", "Driver",
            (_, k, _) => Ratio(k.CurrentDamageFreeStreak, 10)),

        new("company_10_deliveries", "Open for Business", "Company completes 10 deliveries.", "🏢", "Company",
            (c, _, _) => Ratio(c.TotalDeliveries, 10)),
        new("company_10k_miles", "10,000 Mile Fleet", "Company drives 10,000 miles.", "🚚", "Company",
            (c, _, _) => Ratio(c.TotalMilesDriven, 10_000)),
        new("company_100_damage_free", "Spotless Record", "Company completes 100 damage-free deliveries.", "✨", "Company",
            (c, _, _) => Ratio(c.DamageFreeDeliveries, 100)),
        new("company_20_cities", "Coast to Coast", "Company delivers to 20 different cities.", "🌎", "Company",
            (c, _, _) => Ratio(c.VisitedCities.Count, 20)),
    };

    /// <summary>
    /// Re-evaluates all achievements. <paramref name="lastTrip"/> is the trip that just finished (or null).
    /// Records new unlocks in <paramref name="career"/> and returns them.
    /// </summary>
    public static IReadOnlyList<AchievementItem> Evaluate(VirtualCompanyProfile company, CareerProfile career, DeliveryTripRecord? lastTrip, DateTimeOffset now)
    {
        var unlocked = new List<AchievementItem>();
        foreach (var d in Definitions)
        {
            if (career.UnlockedAchievements.ContainsKey(d.Id)) continue;
            if (d.Progress(company, career, lastTrip) >= 1.0)
            {
                career.UnlockedAchievements[d.Id] = now;
                unlocked.Add(ToItem(d, company, career, null));
            }
        }
        return unlocked;
    }

    /// <summary>All achievements with unlock state and progress, for display.</summary>
    public static IReadOnlyList<AchievementItem> List(VirtualCompanyProfile company, CareerProfile career) =>
        Definitions.Select(d => ToItem(d, company, career, null)).ToList();

    private static AchievementItem ToItem(Definition d, VirtualCompanyProfile c, CareerProfile k, DeliveryTripRecord? t)
    {
        bool unlocked = k.UnlockedAchievements.TryGetValue(d.Id, out var at);
        return new AchievementItem
        {
            Id = d.Id,
            Title = d.Title,
            Description = d.Description,
            Icon = d.Icon,
            Category = d.Category,
            IsUnlocked = unlocked,
            UnlockedAt = unlocked ? at : null,
            // Per-trip achievements have no meaningful running progress outside their trip.
            ProgressPercent = unlocked ? 100 : Math.Round(d.Progress(c, k, t) * 100, 0)
        };
    }

    /// <summary>Called for every recorded mile chunk; resets on harsh braking.</summary>
    public static void AddMiles(CareerProfile career, double miles) => career.MilesSinceHarshBraking += Math.Max(0, miles);
    public static void OnHarshBraking(CareerProfile career) => career.MilesSinceHarshBraking = 0;
}
