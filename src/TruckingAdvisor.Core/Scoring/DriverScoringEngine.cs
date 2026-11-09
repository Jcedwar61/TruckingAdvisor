using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Core.Scoring;

/// <summary>
/// Evaluates a trip across all 10 required dimensions and normalizes penalties by distance
/// so long-haul trips (e.g., 1,200 miles) are never unfairly penalized compared to short runs.
/// </summary>
public sealed class DriverScoringEngine
{
    public TripScoreBreakdown CalculateTripScore(DeliveryTripRecord trip, double targetMpg = 6.2)
    {
        // Normalize per 100 miles driven (floor at 25 miles so very short yard hops stay stable)
        double hundredMileUnits = Math.Max(0.25, trip.DistanceMiles / 100.0);

        double speedingPer100 = trip.SpeedingIncidents / hundredMileUnits;
        double brakingPer100 = trip.HarshBrakingIncidents / hundredMileUnits;
        double accelPer100 = trip.AggressiveAccelerationIncidents / hundredMileUnits;
        double overRevPer100 = trip.OverRevIncidents / hundredMileUnits;

        double speedScore = Math.Clamp(100.0 - (speedingPer100 * 6.5), 0.0, 100.0);
        double brakingScore = Math.Clamp(100.0 - (brakingPer100 * 9.0), 0.0, 100.0);
        double accelScore = Math.Clamp(100.0 - (accelPer100 * 7.0), 0.0, 100.0);
        double engineScore = Math.Clamp(100.0 - (overRevPer100 * 6.0), 0.0, 100.0);

        // Unknown fuel economy is scored neutral rather than punished.
        double fuelScore = trip.AverageMpg > 0 && targetMpg > 0
            ? Math.Clamp(85.0 + (trip.AverageMpg / targetMpg - 1.0) * 50.0, 40.0, 100.0)
            : 85.0;

        double truckDamageScore = Math.Clamp(100.0 - (trip.AddedTruckDamagePercent * 12.0), 0.0, 100.0);
        double trailerDamageScore = Math.Clamp(100.0 - (trip.AddedTrailerDamagePercent * 16.0), 0.0, 100.0);

        double completionScore = trip.State == Shared.Enums.DeliveryState.Completed ? 100.0 :
                                 trip.State == Shared.Enums.DeliveryState.Cancelled ? 25.0 : 90.0;
        double punctualityScore = trip.DeliveredOnTime switch { true => 100.0, false => 55.0, null => 100.0 };

        int totalIncidents = trip.SpeedingIncidents + trip.HarshBrakingIncidents + trip.AggressiveAccelerationIncidents + trip.OverRevIncidents;
        double consistencyScore = Math.Clamp(100.0 - ((totalIncidents / hundredMileUnits) * 4.0), 0.0, 100.0);

        return new TripScoreBreakdown
        {
            SpeedCompliance = Math.Round(speedScore, 1),
            BrakingSmoothness = Math.Round(brakingScore, 1),
            AccelerationSmoothness = Math.Round(accelScore, 1),
            FuelEfficiency = Math.Round(fuelScore, 1),
            EngineManagement = Math.Round(engineScore, 1),
            TruckDamageScore = Math.Round(truckDamageScore, 1),
            TrailerDamageScore = Math.Round(trailerDamageScore, 1),
            DeliveryCompletion = Math.Round(completionScore, 1),
            Punctuality = Math.Round(punctualityScore, 1),
            Consistency = Math.Round(consistencyScore, 1)
        };
    }
}
