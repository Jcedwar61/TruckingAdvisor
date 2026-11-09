using TruckingAdvisor.Shared.Enums;

namespace TruckingAdvisor.Shared.Models;

public sealed class DrivingIncident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string IncidentType { get; set; } = string.Empty; // Speeding, HarshBraking, AggressiveAccel, OverRev, UnsafeOperation
    public string Severity { get; set; } = "Normal";
    public float SpeedAtEventMph { get; set; }
    public float Intensity { get; set; }
    public string LocationState { get; set; } = string.Empty;
    public string LocationCity { get; set; } = string.Empty;
    public string AdvisoryMessage { get; set; } = string.Empty;
}

public sealed class TripScoreBreakdown
{
    public double SpeedCompliance { get; set; } = 100.0;
    public double BrakingSmoothness { get; set; } = 100.0;
    public double AccelerationSmoothness { get; set; } = 100.0;
    public double FuelEfficiency { get; set; } = 100.0;
    public double EngineManagement { get; set; } = 100.0;
    public double TruckDamageScore { get; set; } = 100.0;
    public double TrailerDamageScore { get; set; } = 100.0;
    public double DeliveryCompletion { get; set; } = 100.0;
    public double Punctuality { get; set; } = 100.0;
    public double Consistency { get; set; } = 100.0;

    public int FinalScore => (int)Math.Round(
        SpeedCompliance * 0.16 +
        BrakingSmoothness * 0.12 +
        AccelerationSmoothness * 0.10 +
        FuelEfficiency * 0.10 +
        EngineManagement * 0.08 +
        TruckDamageScore * 0.12 +
        TrailerDamageScore * 0.12 +
        DeliveryCompletion * 0.08 +
        Punctuality * 0.06 +
        Consistency * 0.06);

    public string RatingLabel => FinalScore switch
    {
        >= 95 => "Legendary Trucker",
        >= 90 => "Professional Driver",
        >= 80 => "Experienced Driver",
        >= 70 => "Developing Driver",
        _ => "Needs Improvement"
    };
}

public sealed class DeliveryTripRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public DeliveryState State { get; set; } = DeliveryState.NoJob;
    public string TruckMakeModel { get; set; } = string.Empty;
    public string Cargo { get; set; } = string.Empty;
    public float CargoWeightLbs { get; set; }
    public string Origin { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public float DistanceMiles { get; set; }
    public float PlannedDistanceMiles { get; set; }
    public TimeSpan DrivingDuration { get; set; }
    public float AverageSpeedMph { get; set; }
    public float MaxSpeedMph { get; set; }
    public float FuelUsedGallons { get; set; }
    public float AverageMpg { get; set; }
    public int SpeedingIncidents { get; set; }
    public int HarshBrakingIncidents { get; set; }
    public int AggressiveAccelerationIncidents { get; set; }
    public int OverRevIncidents { get; set; }
    public float AddedTruckDamagePercent { get; set; }
    public float AddedTrailerDamagePercent { get; set; }
    /// <summary>Job income reported by the game (in-game currency). Not real money.</summary>
    public ulong RecordedRevenue { get; set; }
    /// <summary>Null when the game gave no deadline or we could not tell.</summary>
    public bool? DeliveredOnTime { get; set; }
    /// <summary>True if the app started after the job was already underway (stats are partial).</summary>
    public bool StartedMidDelivery { get; set; }
    public TripScoreBreakdown ScoreBreakdown { get; set; } = new();
}

public sealed class TruckerPersonality
{
    public string Id { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string HomeState { get; set; } = string.Empty;
    public string Truck { get; set; } = string.Empty;
    public int YearsExperience { get; set; }
    public string PersonalityTraits { get; set; } = string.Empty;
    public string SpeakingStyle { get; set; } = string.Empty;
    public string Background { get; set; } = string.Empty;
    public List<string> PreferredRoutes { get; set; } = new();
    public string VoiceId { get; set; } = "onyx";
    public float PitchMultiplier { get; set; } = 1.0f;
    public float SpeechRate { get; set; } = 1.0f;
    public List<string> PreferredStates { get; set; } = new();
    public List<string> FavoriteTopics { get; set; } = new();
    public string CurrentFictionalRegion { get; set; } = string.Empty;
    public string CurrentFictionalStatus { get; set; } = string.Empty;
    public RelationshipLevel Relationship { get; set; } = RelationshipLevel.Stranger;
    public int RelationshipPoints { get; set; }
    public int ConversationCount { get; set; }
    public DateTimeOffset? LastSpokenAt { get; set; }
    public List<string> SummarizedMemories { get; set; } = new();
}

public sealed class CBChannelDefinition
{
    public int ChannelNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsDispatcherChannel { get; set; }
    public bool IsEmergencyChannel { get; set; }
}

public sealed class VirtualCompanyProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CompanyName { get; set; } = "My Trucking Company";
    public string Headquarters { get; set; } = string.Empty;
    public string Motto { get; set; } = string.Empty;
    public string PrimaryColorHex { get; set; } = "#F59E0B";
    public string SecondaryColorHex { get; set; } = "#1E293B";
    /// <summary>The player's CB handle. Empty until the player sets one.</summary>
    public string PlayerHandle { get; set; } = string.Empty;
    public string PreferredTruck { get; set; } = string.Empty;
    public DateTimeOffset FoundedDate { get; set; } = DateTimeOffset.UtcNow;
    // Everything below starts at zero and only grows from deliveries this app recorded.
    public double TotalMilesDriven { get; set; }
    public int TotalDeliveries { get; set; }
    public int DamageFreeDeliveries { get; set; }
    /// <summary>Sum of in-game job income from recorded deliveries (in-game currency).</summary>
    public decimal RecordedRevenue { get; set; }
    public double TotalFuelGallons { get; set; }
    public double LifetimeAverageMpg => TotalFuelGallons > 0 ? TotalMilesDriven / TotalFuelGallons : 0;
    public double AverageDriverRating { get; set; }
    /// <summary>Destination cities of recorded deliveries (the SDK does not report states).</summary>
    public HashSet<string> VisitedCities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string DisplayHandle => string.IsNullOrWhiteSpace(PlayerHandle) ? "driver" : PlayerHandle;
}

public sealed class CareerProfile
{
    public int Level { get; set; } = 1;
    public long CurrentXp { get; set; }
    public long NextLevelXp { get; set; } = 1000;
    public int CurrentDamageFreeStreak { get; set; }
    public int BestDamageFreeStreak { get; set; }
    public int PersonalBestScore { get; set; }
    public double BestTripMpg { get; set; }

    public string RankTitle => Level switch
    {
        >= 50 => "Legendary Trucker",
        >= 30 => "Road Veteran",
        >= 20 => "Professional Operator",
        >= 10 => "Interstate Trucker",
        >= 5 => "Regional Hauler",
        _ => "Rookie"
    };
}

public sealed class AchievementItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = "🏆";
    public bool IsUnlocked { get; set; }
    public DateTimeOffset? UnlockedAt { get; set; }
    public double ProgressPercent { get; set; }
}

public sealed class ServiceRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TruckMakeModel { get; set; } = string.Empty;
    public DateTimeOffset ServiceDate { get; set; } = DateTimeOffset.UtcNow;
    public float OdometerAtServiceMiles { get; set; }
    public float NextServiceDueOdometerMiles { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal CostEstimate { get; set; }
}
