using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Core.Coaching;

/// <summary>
/// Deterministic telemetry analyzer for real-time driving safety, speeding, harsh braking,
/// aggressive acceleration, over-revving, and mechanical hazards.
/// Never relies on AI for threshold detection.
/// </summary>
public sealed class DrivingAnalyzer : IDrivingAnalyzer
{
    private readonly Dictionary<string, DateTimeOffset> _cooldownTracker = new();
    public TimeSpan DefaultCooldown { get; set; } = TimeSpan.FromSeconds(25);
    public float HarshBrakingMphPerSecThreshold { get; set; } = 9.5f;
    public float AggressiveAccelMphPerSecThreshold { get; set; } = 7.5f;
    /// <summary>Fixed over-rev RPM. When null, uses <see cref="OverRevFractionOfMax"/> of the truck's max RPM (engines differ).</summary>
    public float? OverRevRpmOverride { get; set; }
    public float OverRevFractionOfMax { get; set; } = 0.92f;
    /// <summary>Used only when the game does not report max RPM.</summary>
    public float FallbackOverRevRpm { get; set; } = 1950f;
    public float CriticalFuelPercent { get; set; } = 5f;
    public float SeriousDamageFraction { get; set; } = 0.5f;
    public float CriticalAirPressurePsi { get; set; } = 65f;
    public float HighEngineTempFahrenheit { get; set; } = 225f;

    public event EventHandler<DrivingIncident>? IncidentDetected;

    public void EvaluateSnapshot(AtsTelemetrySnapshot current, AtsTelemetrySnapshot? previous)
    {
        if (current.IsPaused) return;
        _now = current.Timestamp;

        // 1. Safety-Critical Mechanical & Operational Checks (Highest Priority)
        if (current.ParkingBrake && current.SpeedMph > 3.0f)
        {
            EmitIfCooledDown("ParkingBrakeWhileMoving", new DrivingIncident
            {
                IncidentType = "UnsafeOperation",
                Severity = "Critical",
                SpeedAtEventMph = current.SpeedMph,
                LocationState = current.CurrentState,
                LocationCity = current.CurrentCity,
                AdvisoryMessage = "Warning! Your parking brake is still engaged while rolling. Release it immediately."
            }, TimeSpan.FromSeconds(10));
        }

        // Air is normally low right after engine start, so only warn while rolling.
        if (current.EngineEnabled && current.SpeedMph > 2.0f &&
            (current.AirPressureEmergency || (current.AirPressurePsi > 0f && current.AirPressurePsi < CriticalAirPressurePsi)))
        {
            EmitIfCooledDown("LowAirPressure", new DrivingIncident
            {
                IncidentType = "UnsafeOperation",
                Severity = "Critical",
                SpeedAtEventMph = current.SpeedMph,
                Intensity = CriticalAirPressurePsi - current.AirPressurePsi,
                LocationState = current.CurrentState,
                LocationCity = current.CurrentCity,
                AdvisoryMessage = $"Brake air pressure is critically low at {current.AirPressurePsi:F0} PSI. Pull over safely and build air pressure."
            }, TimeSpan.FromSeconds(20));
        }

        if (current.EngineEnabled && current.WaterTempFahrenheit >= HighEngineTempFahrenheit)
        {
            EmitIfCooledDown("HighEngineTemp", new DrivingIncident
            {
                IncidentType = "UnsafeOperation",
                Severity = "Critical",
                SpeedAtEventMph = current.SpeedMph,
                Intensity = current.WaterTempFahrenheit,
                LocationState = current.CurrentState,
                LocationCity = current.CurrentCity,
                AdvisoryMessage = $"Engine coolant temperature is running hot at {current.WaterTempFahrenheit:F0}°F. Ease off the throttle."
            }, TimeSpan.FromSeconds(30));
        }

        if (current.HasFuelCapacity && current.FuelPercent <= CriticalFuelPercent)
        {
            EmitIfCooledDown("CriticalFuel", new DrivingIncident
            {
                IncidentType = "UnsafeOperation",
                Severity = "High",
                SpeedAtEventMph = current.SpeedMph,
                Intensity = current.FuelPercent,
                AdvisoryMessage = $"Fuel is critical at {current.FuelPercent:F0} percent. Find diesel as soon as you can."
            }, TimeSpan.FromMinutes(3));
        }

        float worstWear = MathF.Max(current.DamageEngine, MathF.Max(current.DamageTransmission, current.DamageWheels));
        if (worstWear >= SeriousDamageFraction)
        {
            EmitIfCooledDown("SeriousDamage", new DrivingIncident
            {
                IncidentType = "UnsafeOperation",
                Severity = "High",
                SpeedAtEventMph = current.SpeedMph,
                Intensity = worstWear * 100f,
                AdvisoryMessage = $"Truck damage is serious at {worstWear * 100f:F0} percent. Get to a service shop."
            }, TimeSpan.FromMinutes(5));
        }

        // 2. Speeding Tiers (1-5 Gentle, 6-10 Moderate, 11+ Serious)
        if (current.SpeedLimitMph > 5f && current.SpeedMph > current.SpeedLimitMph)
        {
            float overLimit = current.SpeedMph - current.SpeedLimitMph;
            var severity = ClassifySpeeding(overLimit);
            if (severity != SpeedingSeverity.None)
            {
                string message = severity switch
                {
                    SpeedingSeverity.GentleAdvisory => $"Easy there, driver. You're creeping about {overLimit:F0} over the posted {current.SpeedLimitMph:F0} limit.",
                    SpeedingSeverity.ModerateWarning => $"Easy there, driver. You're running about {overLimit:F0} over the limit.",
                    SpeedingSeverity.SeriousWarning => $"Slow it down, driver! You're {overLimit:F0} MPH over the {current.SpeedLimitMph:F0} MPH limit—DOT won't let that slide.",
                    _ => string.Empty
                };

                EmitIfCooledDown($"Speeding_{severity}", new DrivingIncident
                {
                    IncidentType = "Speeding",
                    Severity = severity == SpeedingSeverity.SeriousWarning ? "High" : "Normal",
                    SpeedAtEventMph = current.SpeedMph,
                    Intensity = overLimit,
                    LocationState = current.CurrentState,
                    LocationCity = current.CurrentCity,
                    AdvisoryMessage = message
                }, DefaultCooldown);
            }
        }

        // 3. Over-Revving Check
        float overRev = OverRevRpmOverride ?? (current.MaxEngineRpm > 0 ? current.MaxEngineRpm * OverRevFractionOfMax : FallbackOverRevRpm);
        if (current.EngineEnabled && current.EngineRpm >= overRev)
        {
            EmitIfCooledDown("OverRev", new DrivingIncident
            {
                IncidentType = "OverRev",
                Severity = "Normal",
                SpeedAtEventMph = current.SpeedMph,
                Intensity = current.EngineRpm,
                LocationState = current.CurrentState,
                LocationCity = current.CurrentCity,
                AdvisoryMessage = $"High RPMs ({current.EngineRpm:F0} RPM). Upshift to stay in the torque sweet spot and save diesel."
            }, TimeSpan.FromSeconds(18));
        }

        // 4. Longitudinal Dynamics (Harsh Braking & Aggressive Acceleration)
        if (previous is not null)
        {
            double dt = (current.Timestamp - previous.Timestamp).TotalSeconds;
            if (dt is > 0.02 and < 2.0)
            {
                float deltaMphPerSec = (float)((current.SpeedMph - previous.SpeedMph) / dt);

                if (deltaMphPerSec <= -HarshBrakingMphPerSecThreshold && current.Brake > 0.5f)
                {
                    EmitIfCooledDown("HarshBraking", new DrivingIncident
                    {
                        IncidentType = "HarshBraking",
                        Severity = "High",
                        SpeedAtEventMph = current.SpeedMph,
                        Intensity = Math.Abs(deltaMphPerSec),
                        LocationState = current.CurrentState,
                        LocationCity = current.CurrentCity,
                        AdvisoryMessage = "Hard braking detected! Leave extra following distance and use your engine brake early."
                    }, TimeSpan.FromSeconds(12));
                }
                else if (deltaMphPerSec >= AggressiveAccelMphPerSecThreshold && current.Throttle > 0.85f)
                {
                    EmitIfCooledDown("AggressiveAccel", new DrivingIncident
                    {
                        IncidentType = "AggressiveAcceleration",
                        Severity = "Normal",
                        SpeedAtEventMph = current.SpeedMph,
                        Intensity = deltaMphPerSec,
                        LocationState = current.CurrentState,
                        LocationCity = current.CurrentCity,
                        AdvisoryMessage = "Heavy throttle surge detected. Progressive throttle application improves fuel economy by up to 12%."
                    }, TimeSpan.FromSeconds(20));
                }
            }
        }
    }

    public static SpeedingSeverity ClassifySpeeding(float mphOverLimit) => mphOverLimit switch
    {
        >= 11.0f => SpeedingSeverity.SeriousWarning,
        >= 6.0f => SpeedingSeverity.ModerateWarning,
        >= 1.0f => SpeedingSeverity.GentleAdvisory,
        _ => SpeedingSeverity.None
    };

    private DateTimeOffset _now;

    private void EmitIfCooledDown(string key, DrivingIncident incident, TimeSpan cooldown)
    {
        var now = _now;
        incident.Timestamp = now;
        if (_cooldownTracker.TryGetValue(key, out var lastFired) && (now - lastFired) < cooldown)
            return;

        _cooldownTracker[key] = now;
        IncidentDetected?.Invoke(this, incident);
    }
}
