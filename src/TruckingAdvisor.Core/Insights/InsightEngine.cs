using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Core.Insights;

/// <summary>
/// Personal driving insights computed only from trips this app recorded. Nothing is invented:
/// an insight appears only when there's enough history to support it.
/// </summary>
public static class InsightEngine
{
    /// <param name="trips">Recorded trips, any order.</param>
    /// <param name="currentTruck">"Make Model" of the truck being driven, or empty.</param>
    public static IReadOnlyList<string> Generate(IEnumerable<DeliveryTripRecord> trips, CareerProfile career, string currentTruck, DateTimeOffset now)
    {
        var done = trips.Where(t => t.State == DeliveryState.Completed).OrderByDescending(t => t.StartedAt).ToList();
        var insights = new List<string>();

        // Fuel economy trend: last 5 vs the 5 before.
        var withMpg = done.Where(t => t.AverageMpg > 0 && t.DistanceMiles >= 50).ToList();
        if (withMpg.Count >= 6)
        {
            double recent = withMpg.Take(5).Average(t => t.AverageMpg);
            double before = withMpg.Skip(5).Take(5).Average(t => t.AverageMpg);
            if (recent >= before + 0.2) insights.Add($"Your fuel economy over your last 5 trips ({recent:F1} MPG) is up from {before:F1} MPG.");
            else if (recent <= before - 0.2) insights.Add($"Your fuel economy has slipped to {recent:F1} MPG over your last 5 trips, from {before:F1} MPG.");
        }

        // Damage-free run.
        double cleanMiles = 0;
        int cleanTrips = 0;
        foreach (var t in done)
        {
            if (t.AddedTruckDamagePercent >= 0.5f || t.AddedTrailerDamagePercent >= 0.5f) break;
            cleanMiles += t.DistanceMiles;
            cleanTrips++;
        }
        if (cleanTrips >= 2 && cleanMiles >= 300)
            insights.Add($"You've driven {cleanMiles:N0} miles over {cleanTrips} deliveries without damaging your truck.");

        // Braking trend per 100 miles.
        var braking = done.Where(t => t.DistanceMiles >= 50).ToList();
        if (braking.Count >= 6)
        {
            static double Rate(IEnumerable<DeliveryTripRecord> ts) =>
                ts.Sum(t => t.HarshBrakingIncidents) * 100.0 / Math.Max(1, ts.Sum(t => t.DistanceMiles));
            double recent = Rate(braking.Take(5)), before = Rate(braking.Skip(5).Take(5));
            if (before > 0 && recent < before * 0.7) insights.Add("Your braking has been smoother lately: fewer hard stops per 100 miles than before.");
        }

        // Regular destination this week.
        var week = done.Where(t => now - t.StartedAt <= TimeSpan.FromDays(7) && !string.IsNullOrEmpty(t.Destination))
            .GroupBy(t => t.Destination, StringComparer.OrdinalIgnoreCase)
            .Select(g => (City: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .FirstOrDefault();
        if (week.Count >= 3) insights.Add($"That's {week.Count} deliveries to {week.City} this week. You're becoming a regular.");

        // This truck's economy.
        if (!string.IsNullOrWhiteSpace(currentTruck) && career.TruckEconomy.TryGetValue(currentTruck, out var econ) && econ.Trips >= 2 && econ.AverageMpg > 0)
            insights.Add($"In the {currentTruck} you average {econ.AverageMpg:F1} MPG over {econ.Trips} recorded trips.");

        return insights;
    }
}
