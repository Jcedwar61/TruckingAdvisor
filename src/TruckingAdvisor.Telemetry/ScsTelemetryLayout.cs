namespace TruckingAdvisor.Telemetry;

/// <summary>
/// Byte offsets into the <c>Local\SCSTelemetry</c> shared memory written by
/// RenCloud/scs-sdk-plugin (layout revision 12, release V.1.12.x).
///
/// Every offset was computed with <c>offsetof()</c> on <c>scsTelemetryMap_t</c> from
/// scs-telemetry/inc/scs-telemetry-common.hpp (x64, natural alignment) — not typed by hand.
/// If the plugin's revision is not <see cref="SupportedRevision"/>, the reader refuses to
/// trust these offsets instead of showing wrong numbers.
///
/// Units are the SCS SDK's: m/s, liters, liters/km, km, meters, seconds, psi, °C, kg.
/// </summary>
public static class ScsTelemetryLayout
{
    public const string MapName = @"Local\SCSTelemetry";
    public const int MapSize = 32 * 1024;
    public const uint SupportedRevision = 12;
    public const int StringSize = 64;

    /// <summary>Bytes the parser needs (covers trailer[0]'s name string).</summary>
    public const int RequiredBytes = TrailerName + StringSize;

    // Header
    public const int SdkActive = 0;            // bool
    public const int Paused = 4;               // bool
    public const int Time = 8;                 // ulong, microseconds
    public const int PluginRevision = 40;      // uint
    public const int GameId = 52;              // uint: 1 ETS2, 2 ATS

    // unsigned ints
    public const int GameTimeAbs = 64;         // uint, game minutes
    public const int JobDeliveryTimeAbs = 88;  // uint, game minutes (deadline)
    public const int PlannedDistanceKm = 100;  // uint, km
    public const int RetarderBrake = 108;      // uint, current retarder step

    // ints
    public const int RestStop = 500;           // int, in-game minutes until rest
    public const int Gear = 504;               // int
    public const int GearDashboard = 508;      // int

    // floats
    public const int TimeScale = 700;
    public const int FuelCapacity = 704;       // liters
    public const int EngineRpmMax = 740;
    public const int CargoMass = 748;          // kg
    public const int Speed = 948;              // m/s (negative when reversing)
    public const int EngineRpm = 952;
    public const int GameThrottle = 976;       // 0..1, effective input
    public const int GameBrake = 980;          // 0..1
    public const int GameClutch = 984;         // 0..1
    public const int CruiseControlSpeed = 988; // m/s
    public const int AirPressure = 992;        // psi
    public const int Fuel = 1000;              // liters
    public const int FuelAvgConsumption = 1004;// liters/km
    public const int FuelRange = 1008;         // km
    public const int OilPressure = 1016;       // psi
    public const int WaterTemperature = 1024;  // °C
    public const int WearEngine = 1036;        // 0..1
    public const int WearTransmission = 1040;
    public const int WearCabin = 1044;
    public const int WearChassis = 1048;
    public const int WearWheels = 1052;
    public const int Odometer = 1056;          // km
    public const int RouteDistance = 1060;     // meters
    public const int RouteTime = 1064;         // seconds
    public const int SpeedLimit = 1068;        // m/s
    public const int JobCargoDamage = 1468;    // 0..1

    // bools
    public const int SpecialJob = 1565;
    public const int ParkBrake = 1566;
    public const int MotorBrake = 1567;
    public const int AirPressureWarning = 1568;
    public const int AirPressureEmergency = 1569;
    public const int FuelWarning = 1570;
    public const int OilPressureWarning = 1572;
    public const int WaterTemperatureWarning = 1573;
    public const int BatteryVoltageWarning = 1574;
    public const int ElectricEnabled = 1575;
    public const int EngineEnabled = 1576;
    public const int CruiseControl = 1589;

    // doubles (world placement)
    public const int CoordinateX = 2200;
    public const int CoordinateY = 2208;       // height, meters
    public const int CoordinateZ = 2216;
    public const int RotationX = 2224;         // heading, 0..1 of a turn, counter-clockwise from north

    // 64-byte strings
    public const int TruckBrand = 2364;
    public const int TruckId = 2428;
    public const int TruckName = 2492;
    public const int Cargo = 2620;
    public const int CityDestination = 2748;
    public const int CompanyDestination = 2876;
    public const int CitySource = 3004;
    public const int CompanySource = 3132;

    // unsigned long long
    public const int JobIncome = 4000;

    // special event bools
    public const int OnJob = 4300;
    public const int JobFinished = 4301;
    public const int JobCancelled = 4302;      // flipped (^= true) on each cancellation
    public const int JobDelivered = 4303;      // flipped (^= true) on each delivery

    // trailer[0]
    public const int Trailer0Attached = 6080;  // bool
    public const int Trailer0CargoDamage = 6152;
    public const int Trailer0WearChassis = 6156;
    public const int Trailer0WearWheels = 6160;
    public const int Trailer0WearBody = 6164;
    public const int TrailerId = 6920;         // string
    public const int TrailerName = 7240;       // string
}
