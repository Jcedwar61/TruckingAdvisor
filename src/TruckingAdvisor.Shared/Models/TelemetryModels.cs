using TruckingAdvisor.Shared.Enums;

namespace TruckingAdvisor.Shared.Models;

/// <summary>
/// Immutable snapshot of American Truck Simulator telemetry plus a few derived values.
///
/// Rules:
/// - Every value comes from the game (direct), is calculated from game values, or is left at its
///   "unknown" default. Nothing is made up.
/// - Unknown numbers are 0 (or -1 where 0 is a real value) and unknown text is empty.
///   Use the Has* helpers before showing or reasoning about a value.
/// - The SCS SDK does not report the current city or state, so those stay empty
///   unless a future map integration fills them in.
/// </summary>
public sealed record AtsTelemetrySnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public ConnectionState ConnectionState { get; init; } = ConnectionState.Disconnected;

    /// <summary>True when this snapshot comes from the opt-in demo generator, not the game.</summary>
    public bool IsDemoData { get; init; }
    public bool IsPaused { get; init; }

    /// <summary>scs-sdk-plugin layout revision (12 for RenCloud V.1.12.x). 0 when unknown.</summary>
    public uint PluginRevision { get; init; }
    /// <summary>1 = ETS2, 2 = ATS, 0 = unknown.</summary>
    public uint GameId { get; init; }
    /// <summary>Plugin game-time counter in microseconds; used to detect stale data.</summary>
    public ulong SimulationTimeMicros { get; init; }

    // --- Vehicle (direct) ---
    public string TruckId { get; init; } = string.Empty;
    public string TruckMake { get; init; } = string.Empty;
    public string TruckModel { get; init; } = string.Empty;
    public float SpeedMph { get; init; }
    /// <summary>Posted limit from the in-game navigation. 0 = unknown / no limit shown.</summary>
    public float SpeedLimitMph { get; init; }
    public float EngineRpm { get; init; }
    /// <summary>Engine max RPM from the truck config. 0 = unknown.</summary>
    public float MaxEngineRpm { get; init; }
    public int Gear { get; init; }
    public int DisplayedGear { get; init; }
    public float Throttle { get; init; }       // 0.0 to 1.0
    public float Brake { get; init; }          // 0.0 to 1.0
    public float Clutch { get; init; }         // 0.0 to 1.0
    public bool ElectricEnabled { get; init; }
    public bool EngineEnabled { get; init; }
    public bool ParkingBrake { get; init; }
    public bool CruiseControlOn { get; init; }
    public float CruiseControlSpeedMph { get; init; }
    public uint RetarderStep { get; init; }
    public bool MotorBrakeOn { get; init; }

    // --- Fuel & fluids ---
    public float FuelGallons { get; init; }
    /// <summary>Tank size. 0 = unknown.</summary>
    public float FuelCapacityGallons { get; init; }
    public bool HasFuelCapacity => FuelCapacityGallons > 0f;
    public float FuelPercent => HasFuelCapacity ? (FuelGallons / FuelCapacityGallons) * 100f : 0f;
    /// <summary>Instant MPG calculated by TruckingAdvisor from fuel/odometer deltas. 0 = unknown.</summary>
    public float InstantMpg { get; init; }
    /// <summary>Game's average fuel economy converted to US MPG. 0 = unknown.</summary>
    public float AverageMpg { get; init; }
    public bool HasFuelEconomy => AverageMpg > 0f;
    /// <summary>Range estimate (game value, or calculated). 0 = unknown.</summary>
    public float EstimatedRangeMiles { get; init; }
    public float WaterTempFahrenheit { get; init; }
    public float OilPressurePsi { get; init; }
    public float AirPressurePsi { get; init; }
    public bool AirPressureWarning { get; init; }
    public bool AirPressureEmergency { get; init; }
    public bool FuelWarning { get; init; }
    public bool WaterTempWarning { get; init; }
    public bool OilPressureWarning { get; init; }
    public bool BatteryVoltageWarning { get; init; }

    // --- Damage / wear (direct, 0.0 to 1.0) ---
    public float DamageEngine { get; init; }
    public float DamageTransmission { get; init; }
    public float DamageCabin { get; init; }
    public float DamageChassis { get; init; }
    public float DamageWheels { get; init; }
    public float DamageTrailer { get; init; }
    public float DamageCargo { get; init; }
    public float OverallTruckDamagePercent =>
        Math.Max(Math.Max(DamageEngine, DamageTransmission), Math.Max(DamageCabin, Math.Max(DamageChassis, DamageWheels))) * 100f;

    // --- Trailer & job (direct) ---
    public bool TrailerAttached { get; init; }
    public string TrailerId { get; init; } = string.Empty;
    public string CargoName { get; init; } = string.Empty;
    public float CargoWeightLbs { get; init; }
    public bool JobActive { get; init; }
    public string OriginCity { get; init; } = string.Empty;
    public string OriginCompany { get; init; } = string.Empty;
    public string DestinationCity { get; init; } = string.Empty;
    public string DestinationCompany { get; init; } = string.Empty;
    /// <summary>Not exposed by the SCS SDK. Empty unless a map integration provides it.</summary>
    public string CurrentState { get; init; } = string.Empty;
    /// <summary>Not exposed by the SCS SDK. Empty unless a map integration provides it.</summary>
    public string CurrentCity { get; init; } = string.Empty;
    public float PlannedDistanceMiles { get; init; }
    public float NavigationDistanceRemainingMiles { get; init; }
    public float NavigationTimeRemainingMinutes { get; init; }
    /// <summary>Job income in in-game currency. 0 = unknown / no job.</summary>
    public ulong JobIncome { get; init; }
    /// <summary>Absolute game minute the delivery is due. 0 = unknown / no deadline.</summary>
    public uint JobDeadlineGameMinutes { get; init; }
    public bool SpecialJob { get; init; }
    /// <summary>
    /// The plugin FLIPS this flag on every delivery (it is not "true when delivered").
    /// Detect deliveries by comparing with the value seen earlier.
    /// </summary>
    public bool JobDeliveredFlag { get; init; }
    /// <summary>Flipped on every cancellation; compare with an earlier value.</summary>
    public bool JobCancelledFlag { get; init; }

    // --- World position (direct) & grade (calculated) ---
    public double WorldX { get; init; }
    /// <summary>Height in meters.</summary>
    public double WorldY { get; init; }
    public double WorldZ { get; init; }
    /// <summary>Compass heading: 0 = north, 90 = east, 180 = south, 270 = west.</summary>
    public float HeadingDegrees { get; init; }
    /// <summary>Calculated from position changes; approximate. 0 = level or unknown.</summary>
    public float CalculatedGradePercent { get; init; }
    public float OdometerMiles { get; init; }

    // --- Game time & fatigue ---
    /// <summary>Absolute game time in minutes since the start of the profile's game calendar.</summary>
    public uint GameTimeMinutes { get; init; }
    public float TimeScale { get; init; }
    /// <summary>In-game minutes until the driver needs rest. -1 = unknown (fatigue disabled or no data).</summary>
    public int NextRestStopGameMinutes { get; init; } = -1;
    public bool HasRestData => NextRestStopGameMinutes >= 0;

    public bool IsLive => ConnectionState is ConnectionState.Connected or ConnectionState.GamePaused;

    public string FormattedGameTime
    {
        get
        {
            uint totalMins = GameTimeMinutes % (24 * 60);
            return $"{totalMins / 60:D2}:{totalMins % 60:D2}";
        }
    }

    public bool IsNightDriving
    {
        get
        {
            uint hour = (GameTimeMinutes % (24 * 60)) / 60;
            return hour >= 21 || hour <= 5;
        }
    }
}
