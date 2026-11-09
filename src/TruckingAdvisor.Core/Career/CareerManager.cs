using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Core.Career;

/// <summary>
/// Updates the virtual company and career from trips this app actually recorded.
/// Nothing is imported from or written to the game's own economy.
/// </summary>
public static class CareerManager
{
    public static long XpForTrip(DeliveryTripRecord trip)
    {
        if (trip.State != DeliveryState.Completed) return 0;
        long xp = (long)Math.Round(trip.DistanceMiles * 2);
        xp += trip.ScoreBreakdown.FinalScore * 3;
        if (trip.AddedTruckDamagePercent < 0.5f && trip.AddedTrailerDamagePercent < 0.5f) xp += 100;
        return Math.Max(0, xp);
    }

    public static long XpToNextLevel(int level) => 1000L + (level - 1) * 250L;

    /// <summary>Applies a finished trip. Returns the number of levels gained.</summary>
    public static int ApplyTrip(VirtualCompanyProfile company, CareerProfile career, DeliveryTripRecord trip)
    {
        if (trip.State != DeliveryState.Completed) return 0;

        bool damageFree = trip.AddedTruckDamagePercent < 0.5f && trip.AddedTrailerDamagePercent < 0.5f;
        int previousDeliveries = company.TotalDeliveries;

        company.TotalDeliveries++;
        company.TotalMilesDriven += trip.DistanceMiles;
        company.TotalFuelGallons += trip.FuelUsedGallons;
        company.RecordedRevenue += trip.RecordedRevenue;
        if (damageFree) company.DamageFreeDeliveries++;
        if (!string.IsNullOrWhiteSpace(trip.Destination)) company.VisitedCities.Add(trip.Destination);
        company.AverageDriverRating = (company.AverageDriverRating * previousDeliveries + trip.ScoreBreakdown.FinalScore) / company.TotalDeliveries;

        career.CurrentDamageFreeStreak = damageFree ? career.CurrentDamageFreeStreak + 1 : 0;
        career.BestDamageFreeStreak = Math.Max(career.BestDamageFreeStreak, career.CurrentDamageFreeStreak);
        career.PersonalBestScore = Math.Max(career.PersonalBestScore, trip.ScoreBreakdown.FinalScore);
        if (trip.AverageMpg > 0 && trip.DistanceMiles >= 50) career.BestTripMpg = Math.Max(career.BestTripMpg, trip.AverageMpg);
        if (!string.IsNullOrWhiteSpace(trip.TruckMakeModel) && trip.DistanceMiles > 0 && trip.FuelUsedGallons > 0)
        {
            if (!career.TruckEconomy.TryGetValue(trip.TruckMakeModel, out var econ))
                career.TruckEconomy[trip.TruckMakeModel] = econ = new TruckEconomy();
            econ.Miles += trip.DistanceMiles;
            econ.Gallons += trip.FuelUsedGallons;
            econ.Trips++;
        }

        int levels = 0;
        career.CurrentXp += XpForTrip(trip);
        career.NextLevelXp = XpToNextLevel(career.Level);
        while (career.CurrentXp >= career.NextLevelXp && career.Level < 50)
        {
            career.CurrentXp -= career.NextLevelXp;
            career.Level++;
            levels++;
            career.NextLevelXp = XpToNextLevel(career.Level);
        }
        return levels;
    }
}
