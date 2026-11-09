using System.Buffers.Binary;
using System.Text;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;
using L = TruckingAdvisor.Telemetry.ScsTelemetryLayout;

namespace TruckingAdvisor.Telemetry;

public enum ParseResult
{
    Ok,
    BufferTooSmall,
    SdkInactive,
    UnsupportedRevision
}

/// <summary>
/// Turns a copy of the shared-memory block into an <see cref="AtsTelemetrySnapshot"/>.
/// Pure and allocation-light so it can be unit tested without the game.
/// </summary>
public static class ScsTelemetryParser
{
    public const float MpsToMph = 2.2369363f;
    public const float KmToMiles = 0.62137119f;
    public const float LitersToGallons = 0.26417205f;
    public const float KgToLbs = 2.2046226f;
    /// <summary>US MPG = this / (liters per km).</summary>
    public const float LitersPerKmToMpgFactor = 2.3521458f;

    public static ParseResult TryParse(ReadOnlySpan<byte> m, DateTimeOffset now, out AtsTelemetrySnapshot snapshot)
    {
        snapshot = new AtsTelemetrySnapshot { Timestamp = now };
        if (m.Length < L.RequiredBytes) return ParseResult.BufferTooSmall;
        if (!Bool(m, L.SdkActive)) return ParseResult.SdkInactive;

        uint revision = U32(m, L.PluginRevision);
        if (revision != L.SupportedRevision)
        {
            snapshot = snapshot with { PluginRevision = revision };
            return ParseResult.UnsupportedRevision;
        }

        bool paused = Bool(m, L.Paused);
        float avgLitersPerKm = F32(m, L.FuelAvgConsumption);
        float avgMpg = avgLitersPerKm > 0.0001f ? LitersPerKmToMpgFactor / avgLitersPerKm : 0f;
        float rangeKm = F32(m, L.FuelRange);
        int restStop = I32(m, L.RestStop);
        // SDK heading is 0..1 of a turn, counter-clockwise from north (0.25 = west).
        // Convert to a compass bearing (clockwise, 90 = east).
        double turn = F64(m, L.RotationX) % 1.0;
        if (turn < 0) turn += 1.0;
        float heading = (float)((360.0 - turn * 360.0) % 360.0);

        snapshot = new AtsTelemetrySnapshot
        {
            Timestamp = now,
            ConnectionState = paused ? ConnectionState.GamePaused : ConnectionState.Connected,
            IsPaused = paused,
            PluginRevision = revision,
            GameId = U32(m, L.GameId),
            SimulationTimeMicros = U64(m, L.Time),

            TruckId = Str(m, L.TruckId),
            TruckMake = Str(m, L.TruckBrand),
            TruckModel = Str(m, L.TruckName),
            SpeedMph = MathF.Abs(F32(m, L.Speed)) * MpsToMph,
            SpeedLimitMph = MathF.Max(0f, F32(m, L.SpeedLimit)) * MpsToMph,
            EngineRpm = F32(m, L.EngineRpm),
            MaxEngineRpm = F32(m, L.EngineRpmMax),
            Gear = I32(m, L.Gear),
            DisplayedGear = I32(m, L.GearDashboard),
            Throttle = Clamp01(F32(m, L.GameThrottle)),
            Brake = Clamp01(F32(m, L.GameBrake)),
            Clutch = Clamp01(F32(m, L.GameClutch)),
            ElectricEnabled = Bool(m, L.ElectricEnabled),
            EngineEnabled = Bool(m, L.EngineEnabled),
            ParkingBrake = Bool(m, L.ParkBrake),
            MotorBrakeOn = Bool(m, L.MotorBrake),
            RetarderStep = U32(m, L.RetarderBrake),
            CruiseControlOn = Bool(m, L.CruiseControl),
            CruiseControlSpeedMph = F32(m, L.CruiseControlSpeed) * MpsToMph,

            FuelGallons = F32(m, L.Fuel) * LitersToGallons,
            FuelCapacityGallons = F32(m, L.FuelCapacity) * LitersToGallons,
            AverageMpg = avgMpg,
            EstimatedRangeMiles = rangeKm > 0 ? rangeKm * KmToMiles : 0f,
            AirPressurePsi = F32(m, L.AirPressure),              // already psi
            OilPressurePsi = F32(m, L.OilPressure),              // already psi
            WaterTempFahrenheit = F32(m, L.WaterTemperature) * 9f / 5f + 32f,
            AirPressureWarning = Bool(m, L.AirPressureWarning),
            AirPressureEmergency = Bool(m, L.AirPressureEmergency),
            FuelWarning = Bool(m, L.FuelWarning),
            OilPressureWarning = Bool(m, L.OilPressureWarning),
            WaterTempWarning = Bool(m, L.WaterTemperatureWarning),
            BatteryVoltageWarning = Bool(m, L.BatteryVoltageWarning),

            DamageEngine = F32(m, L.WearEngine),
            DamageTransmission = F32(m, L.WearTransmission),
            DamageCabin = F32(m, L.WearCabin),
            DamageChassis = F32(m, L.WearChassis),
            DamageWheels = F32(m, L.WearWheels),
            DamageTrailer = MathF.Max(F32(m, L.Trailer0WearChassis), MathF.Max(F32(m, L.Trailer0WearWheels), F32(m, L.Trailer0WearBody))),
            DamageCargo = MathF.Max(F32(m, L.JobCargoDamage), F32(m, L.Trailer0CargoDamage)),

            TrailerAttached = Bool(m, L.Trailer0Attached),
            TrailerId = Str(m, L.TrailerId),
            CargoName = Str(m, L.Cargo),
            CargoWeightLbs = F32(m, L.CargoMass) * KgToLbs,
            JobActive = Bool(m, L.OnJob),
            OriginCity = Str(m, L.CitySource),
            OriginCompany = Str(m, L.CompanySource),
            DestinationCity = Str(m, L.CityDestination),
            DestinationCompany = Str(m, L.CompanyDestination),
            PlannedDistanceMiles = U32(m, L.PlannedDistanceKm) * KmToMiles,
            NavigationDistanceRemainingMiles = F32(m, L.RouteDistance) / 1000f * KmToMiles,
            NavigationTimeRemainingMinutes = F32(m, L.RouteTime) / 60f,
            JobIncome = U64(m, L.JobIncome),
            JobDeadlineGameMinutes = U32(m, L.JobDeliveryTimeAbs),
            SpecialJob = Bool(m, L.SpecialJob),
            JobDeliveredFlag = Bool(m, L.JobDelivered),
            JobCancelledFlag = Bool(m, L.JobCancelled),

            WorldX = F64(m, L.CoordinateX),
            WorldY = F64(m, L.CoordinateY),
            WorldZ = F64(m, L.CoordinateZ),
            HeadingDegrees = heading,
            OdometerMiles = F32(m, L.Odometer) * KmToMiles,

            GameTimeMinutes = U32(m, L.GameTimeAbs),
            TimeScale = F32(m, L.TimeScale),
            NextRestStopGameMinutes = restStop >= 0 ? restStop : -1
        };
        return ParseResult.Ok;
    }

    private static bool Bool(ReadOnlySpan<byte> m, int o) => m[o] != 0;
    private static uint U32(ReadOnlySpan<byte> m, int o) => BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(o, 4));
    private static int I32(ReadOnlySpan<byte> m, int o) => BinaryPrimitives.ReadInt32LittleEndian(m.Slice(o, 4));
    private static ulong U64(ReadOnlySpan<byte> m, int o) => BinaryPrimitives.ReadUInt64LittleEndian(m.Slice(o, 8));
    private static float F32(ReadOnlySpan<byte> m, int o) => Finite(BinaryPrimitives.ReadSingleLittleEndian(m.Slice(o, 4)));
    private static double F64(ReadOnlySpan<byte> m, int o)
    {
        double v = BinaryPrimitives.ReadDoubleLittleEndian(m.Slice(o, 8));
        return double.IsFinite(v) ? v : 0.0;
    }
    private static float Finite(float v) => float.IsFinite(v) ? v : 0f;
    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

    private static string Str(ReadOnlySpan<byte> m, int o)
    {
        var bytes = m.Slice(o, L.StringSize);
        int nul = bytes.IndexOf((byte)0);
        if (nul >= 0) bytes = bytes[..nul];
        return Encoding.UTF8.GetString(bytes).Trim();
    }
}
