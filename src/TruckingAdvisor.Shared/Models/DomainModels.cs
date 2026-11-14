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
    // --- Money and events reported by the game during this trip (in-game currency; 0 = none / unknown) ---
    /// <summary>What the game paid on delivery. 0 if the game didn't report it (then RecordedRevenue is the offer).</summary>
    public long PaidRevenue { get; set; }
    /// <summary>XP the game awarded for the delivery.</summary>
    public int GameXp { get; set; }
    public long Fines { get; set; }
    public int FineCount { get; set; }
    /// <summary>Offences fined during the trip, e.g. "speeding x2, red light".</summary>
    public List<string> FineOffences { get; set; } = new();
    public long Tolls { get; set; }
    /// <summary>Ferry and train fares.</summary>
    public long TransportCosts { get; set; }
    public long CancelPenalty { get; set; }
    public float RefueledGallons { get; set; }
    public bool UsedAutoPark { get; set; }
    /// <summary>1 = ETS2 (euros), 2 = ATS (dollars), 0 = unknown.</summary>
    public uint GameId { get; set; }
    /// <summary>Game pay minus fines, tolls, fares and penalties recorded during the trip (fuel cost isn't reported by the game).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long NetIncome => (PaidRevenue > 0 ? PaidRevenue : State == DeliveryState.Completed ? (long)RecordedRevenue : 0)
                             - Fines - Tolls - TransportCosts - CancelPenalty;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCosts => Fines > 0 || Tolls > 0 || TransportCosts > 0 || CancelPenalty > 0;

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
    /// <summary>Free Microsoft neural voice as "voice|rate|pitch", e.g. "en-US-ChristopherNeural|-6%|-6Hz". Empty = pick one.</summary>
    public string NeuralVoice { get; set; } = string.Empty;
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

    /// <summary>
    /// Lock for this trucker's changing state (memories, relationship). The radio updates it on a
    /// background thread while the contacts list and the database read it: always copy under this lock.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public object Sync { get; } = new();

    /// <summary>A copy of the memories that's safe to use on any thread.</summary>
    public List<string> MemoriesSnapshot()
    {
        lock (Sync) return SummarizedMemories.ToList();
    }
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
    /// <summary>The company dispatcher's on-air name (a persistent character).</summary>
    public string DispatcherName { get; set; } = "Sarah";
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
    /// <summary>Fines the game charged during recorded deliveries.</summary>
    public long RecordedFines { get; set; }
    /// <summary>Tolls, ferry and train fares during recorded deliveries.</summary>
    public long RecordedTolls { get; set; }
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
    /// <summary>Miles driven (app-recorded) since the last harsh braking event.</summary>
    public double MilesSinceHarshBraking { get; set; }
    /// <summary>Achievement id -> when it was unlocked.</summary>
    public Dictionary<string, DateTimeOffset> UnlockedAchievements { get; set; } = new();
    /// <summary>Per-truck lifetime totals (app-recorded), keyed by "Make Model".</summary>
    public Dictionary<string, TruckEconomy> TruckEconomy { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string RankTitle => Level switch
    {
        >= 50 => "Legendary Trucker",
        >= 30 => "Road Veteran",
        >= 20 => "Professional Operator",
        >= 10 => "Interstate Trucker",
        >= 5 => "Regional Hauler",
        _ => "Rookie Driver"
    };
}

public sealed class TruckEconomy
{
    public double Miles { get; set; }
    public double Gallons { get; set; }
    public int Trips { get; set; }
    public double AverageMpg => Gallons > 0 ? Miles / Gallons : 0;
}

public sealed class AchievementItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = "🏆";
    /// <summary>"Driver" achievements or "Company" milestones.</summary>
    public string Category { get; set; } = "Driver";
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
