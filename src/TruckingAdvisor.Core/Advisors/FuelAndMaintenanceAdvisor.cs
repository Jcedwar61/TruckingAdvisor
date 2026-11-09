using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Core.Advisors;

/// <summary>
/// Slow-changing advice (fuel, maintenance, rest). Call about once a second, not every frame.
/// Only uses values the game reported; skips any advice whose inputs are unknown.
/// Safety-critical alerts (air pressure, parking brake, overheating) live in DrivingAnalyzer.
/// </summary>
public sealed class FuelAndMaintenanceAdvisor
{
    public float LowFuelPercent { get; set; } = 25f;
    public float EngineWearAdvisory { get; set; } = 0.12f;
    public float TrailerWearAdvisory { get; set; } = 0.03f;
    public float ServiceIntervalMiles { get; set; } = 15000f;
    public int RestWarningGameMinutes { get; set; } = 90;

    /// <param name="previousTripMpg">MPG of the last recorded trip, or null if none.</param>
    /// <param name="lastServiceOdometerMiles">Odometer at last logged service, or null if never logged.</param>
    public IReadOnlyList<string> GenerateAdvisories(AtsTelemetrySnapshot snapshot, double? previousTripMpg, float? lastServiceOdometerMiles)
    {
        var advisories = new List<string>();

        if (snapshot.HasFuelCapacity && snapshot.FuelPercent <= LowFuelPercent)
            advisories.Add($"You're down to about {snapshot.FuelPercent:F0}% fuel. Might want to start thinking about diesel.");

        if (snapshot.JobActive && snapshot.NavigationDistanceRemainingMiles > 0 && snapshot.EstimatedRangeMiles > 0)
        {
            string dest = string.IsNullOrEmpty(snapshot.DestinationCity) ? "your destination" : snapshot.DestinationCity;
            if (snapshot.EstimatedRangeMiles >= snapshot.NavigationDistanceRemainingMiles * 1.15f)
            {
                string needed = snapshot.HasFuelEconomy
                    ? $" (about {snapshot.NavigationDistanceRemainingMiles / snapshot.AverageMpg:F0} gal needed)"
                    : string.Empty;
                advisories.Add($"Your current range ({snapshot.EstimatedRangeMiles:F0} mi) should reach {dest}, assuming similar conditions{needed}.");
            }
            else
            {
                advisories.Add($"Plan a fuel stop before {dest}: {snapshot.NavigationDistanceRemainingMiles:F0} mi to go vs about {snapshot.EstimatedRangeMiles:F0} mi of range.");
            }
        }

        if (previousTripMpg is > 0 && snapshot.HasFuelEconomy && snapshot.AverageMpg > previousTripMpg + 0.15)
            advisories.Add($"Your fuel economy ({snapshot.AverageMpg:F1} MPG) is better than your previous trip ({previousTripMpg:F1} MPG).");

        if (snapshot.DamageEngine >= EngineWearAdvisory)
            advisories.Add($"Engine damage is getting high ({snapshot.DamageEngine * 100f:F0}%). Might want to visit a service station.");

        if (snapshot.TrailerAttached && snapshot.DamageTrailer >= TrailerWearAdvisory)
            advisories.Add($"Your trailer shows {snapshot.DamageTrailer * 100f:F1}% damage. Watch tight turns and dock approaches.");

        if (lastServiceOdometerMiles is { } lastService && snapshot.OdometerMiles > 0)
        {
            float milesSinceService = Math.Max(0f, snapshot.OdometerMiles - lastService);
            if (milesSinceService >= ServiceIntervalMiles)
                advisories.Add($"Preventive maintenance due: {milesSinceService:N0} miles since your last logged service.");
        }

        // Deadline risk from the navigation estimate (both in game time).
        if (snapshot.JobActive && snapshot.JobDeadlineGameMinutes > 0 && snapshot.GameTimeMinutes > 0 && snapshot.NavigationTimeRemainingMinutes > 0)
        {
            double slack = snapshot.JobDeadlineGameMinutes - (snapshot.GameTimeMinutes + snapshot.NavigationTimeRemainingMinutes);
            if (slack < 0)
                advisories.Add($"Navigation estimate puts you about {-slack / 60:F1} game hours past the deadline. Don't speed to make it up; arriving safe beats arriving early.");
            else if (snapshot.HasRestData && snapshot.NextRestStopGameMinutes < snapshot.NavigationTimeRemainingMinutes && slack < 9 * 60)
                advisories.Add("You'll need to rest before you arrive, and the deadline is tight once you add a rest stop.");
        }

        if (snapshot.HasRestData && snapshot.NextRestStopGameMinutes <= RestWarningGameMinutes)
            advisories.Add("You've been running a while, driver. Might be a good time to start looking for a place to shut down.");

        return advisories;
    }
}
