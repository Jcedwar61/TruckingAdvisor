using System.Buffers.Binary;
using System.Text;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;
using TruckingAdvisor.Telemetry;
using Xunit;
using L = TruckingAdvisor.Telemetry.ScsTelemetryLayout;

namespace TruckingAdvisor.Core.Tests;

public sealed class TelemetryParserTests
{
    private static byte[] Buffer() => new byte[L.RequiredBytes];
    private static void F(byte[] b, int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), v);
    private static void D(byte[] b, int o, double v) => BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(o), v);
    private static void U(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void I(byte[] b, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
    private static void S(byte[] b, int o, string v) => Encoding.UTF8.GetBytes(v).CopyTo(b, o);

    private static byte[] LiveBuffer()
    {
        var b = Buffer();
        b[L.SdkActive] = 1;
        U(b, L.PluginRevision, 12);
        U(b, L.GameId, 2);
        return b;
    }

    [Fact]
    public void Offsets_MatchRenCloudRevision12Layout()
    {
        // Values computed with offsetof() on scsTelemetryMap_t (scs-telemetry-common.hpp).
        Assert.Equal(948, L.Speed);
        Assert.Equal(952, L.EngineRpm);
        Assert.Equal(1068, L.SpeedLimit);
        Assert.Equal(740, L.EngineRpmMax);
        Assert.Equal(1000, L.Fuel);
        Assert.Equal(704, L.FuelCapacity);
        Assert.Equal(992, L.AirPressure);
        Assert.Equal(1016, L.OilPressure);
        Assert.Equal(1024, L.WaterTemperature);
        Assert.Equal(1566, L.ParkBrake);
        Assert.Equal(2200, L.CoordinateX);
        Assert.Equal(2620, L.Cargo);
        Assert.Equal(4000, L.JobIncome);
        Assert.Equal(4300, L.OnJob);
        Assert.Equal(6080, L.Trailer0Attached);
        Assert.Equal(@"Local\SCSTelemetry", L.MapName);
    }

    [Fact]
    public void Parse_ConvertsSdkUnits_WithoutDoubleConvertingPressure()
    {
        var b = LiveBuffer();
        F(b, L.Speed, 29.0576f);            // m/s -> 65 mph
        F(b, L.SpeedLimit, 31.2928f);       // 70 mph
        F(b, L.EngineRpm, 1450f);
        F(b, L.EngineRpmMax, 2100f);
        F(b, L.AirPressure, 118f);          // already psi
        F(b, L.OilPressure, 45f);           // already psi
        F(b, L.WaterTemperature, 90f);      // °C -> 194 °F
        F(b, L.Fuel, 500f);                 // liters
        F(b, L.FuelCapacity, 1000f);
        F(b, L.FuelAvgConsumption, 0.39f);  // L/km -> ~6.03 MPG
        F(b, L.FuelRange, 1000f);           // km
        F(b, L.RouteDistance, 160934f);     // m -> 100 mi
        F(b, L.RouteTime, 5400f);           // s -> 90 min
        F(b, L.CargoMass, 18143.7f);        // kg -> 40,000 lb
        I(b, L.RestStop, 300);              // game minutes
        I(b, L.GearDashboard, 12);
        U(b, L.GameTimeAbs, 23 * 60 + 45);
        b[L.ParkBrake] = 1;
        b[L.OnJob] = 1;
        b[L.Trailer0Attached] = 1;
        D(b, L.CoordinateY, 1234.5);
        D(b, L.RotationX, 0.25);
        S(b, L.Cargo, "Lumber");
        S(b, L.CityDestination, "Denver");
        S(b, L.TruckBrand, "Peterbilt");

        Assert.Equal(ParseResult.Ok, ScsTelemetryParser.TryParse(b, DateTimeOffset.UnixEpoch, out var s));
        Assert.Equal(ConnectionState.Connected, s.ConnectionState);
        Assert.Equal(65f, s.SpeedMph, 1);
        Assert.Equal(70f, s.SpeedLimitMph, 1);
        Assert.Equal(1450f, s.EngineRpm);
        Assert.Equal(2100f, s.MaxEngineRpm);
        Assert.Equal(118f, s.AirPressurePsi);
        Assert.Equal(45f, s.OilPressurePsi);
        Assert.Equal(194f, s.WaterTempFahrenheit, 1);
        Assert.Equal(50f, s.FuelPercent, 1);
        Assert.Equal(132.09f, s.FuelGallons, 1);
        Assert.Equal(6.03f, s.AverageMpg, 2);
        Assert.Equal(621.4f, s.EstimatedRangeMiles, 1);
        Assert.Equal(100f, s.NavigationDistanceRemainingMiles, 1);
        Assert.Equal(90f, s.NavigationTimeRemainingMinutes, 1);
        Assert.Equal(40000f, s.CargoWeightLbs, 0);
        Assert.Equal(300, s.NextRestStopGameMinutes);
        Assert.Equal(12, s.DisplayedGear);
        Assert.Equal("23:45", s.FormattedGameTime);
        Assert.True(s.ParkingBrake);
        Assert.True(s.JobActive);
        Assert.True(s.TrailerAttached);
        Assert.Equal(1234.5, s.WorldY);
        Assert.Equal(270f, s.HeadingDegrees, 1); // 0.25 turn counter-clockwise = west
        Assert.Equal("Lumber", s.CargoName);
        Assert.Equal("Denver", s.DestinationCity);
        Assert.Equal("Peterbilt", s.TruckMake);
    }

    [Fact]
    public void Parse_LeavesUnknownsEmpty_NeverInventsValues()
    {
        Assert.Equal(ParseResult.Ok, ScsTelemetryParser.TryParse(LiveBuffer(), DateTimeOffset.UnixEpoch, out var s));
        Assert.Equal(0f, s.AverageMpg);
        Assert.False(s.HasFuelEconomy);
        Assert.False(s.HasFuelCapacity);
        Assert.Equal(0f, s.EstimatedRangeMiles);
        Assert.Equal(string.Empty, s.CurrentCity);
        Assert.Equal(string.Empty, s.CurrentState);

        var enriched = new DerivedTelemetryCalculator().Enrich(s, null);
        Assert.Equal(0f, enriched.AverageMpg);
        Assert.Equal(0f, enriched.InstantMpg);
        Assert.Equal(0f, enriched.EstimatedRangeMiles);
    }

    [Fact]
    public void Parse_RejectsInactiveSdkAndUnknownRevision()
    {
        Assert.Equal(ParseResult.SdkInactive, ScsTelemetryParser.TryParse(Buffer(), DateTimeOffset.UnixEpoch, out _));

        var b = LiveBuffer();
        U(b, L.PluginRevision, 11);
        Assert.Equal(ParseResult.UnsupportedRevision, ScsTelemetryParser.TryParse(b, DateTimeOffset.UnixEpoch, out var s));
        Assert.Equal(11u, s.PluginRevision);

        Assert.Equal(ParseResult.BufferTooSmall, ScsTelemetryParser.TryParse(new byte[100], DateTimeOffset.UnixEpoch, out _));
    }

    [Fact]
    public void Parse_TreatsNaNAsUnknown()
    {
        var b = LiveBuffer();
        F(b, L.Speed, float.NaN);
        ScsTelemetryParser.TryParse(b, DateTimeOffset.UnixEpoch, out var s);
        Assert.Equal(0f, s.SpeedMph);
    }

    [Fact]
    public void DerivedCalculator_ComputesGradeAndInstantMpgFromRealDeltas()
    {
        var calc = new DerivedTelemetryCalculator();
        var a = new AtsTelemetrySnapshot { WorldX = 0, WorldY = 100, OdometerMiles = 100f, FuelGallons = 100f };
        calc.Enrich(a, null);
        var b = a with { WorldX = 100, WorldY = 106, OdometerMiles = 100.5f, FuelGallons = 99.9f };
        var r = calc.Enrich(b, a);
        Assert.Equal(6f, r.CalculatedGradePercent, 1);
        Assert.Equal(5f, r.InstantMpg, 1);
    }

    [Fact]
    public void Demo_IsAlwaysFlagged()
    {
        var d = new DemoTelemetryGenerator().Next(DateTimeOffset.UtcNow);
        Assert.True(d.IsDemoData);
        Assert.Equal(ConnectionState.SimulationFallback, d.ConnectionState);
        Assert.False(d.IsLive);
    }

    [Theory]
    [InlineData(new[] { "Win32/scs-telemetry.dll", "Win64/scs-telemetry.dll" }, "Win64/scs-telemetry.dll")]
    [InlineData(new[] { "release/x86/scs-telemetry.dll", "release/x64/scs-telemetry.dll" }, "release/x64/scs-telemetry.dll")]
    [InlineData(new[] { "scs-telemetry.dll" }, "scs-telemetry.dll")]
    [InlineData(new[] { "Win32/scs-telemetry.dll" }, null)]
    [InlineData(new[] { "readme.txt" }, null)]
    public void PluginInstaller_PicksOnly64BitDll(string[] entries, string? expected) =>
        Assert.Equal(expected, GitHubPluginInstallerService.PickX64Dll(entries, "scs-telemetry.dll"));
}
