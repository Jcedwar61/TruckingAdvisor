using System.Globalization;

namespace TruckingAdvisor.Shared;

public enum UnitPreference { Auto, Imperial, Metric }

/// <summary>
/// Display units. Telemetry is kept in one internal form (miles, MPH, gallons, lbs, °F, psi) and
/// converted here for the screen and for speech. "Auto" follows the game: ATS imperial, ETS2 metric.
/// </summary>
public static class Units
{
    /// <summary>True = km, km/h, litres, kg, °C, bar.</summary>
    public static bool Metric { get; set; }

    public static bool Resolve(UnitPreference preference, uint gameId) => preference switch
    {
        UnitPreference.Metric => true,
        UnitPreference.Imperial => false,
        _ => gameId == 1 // ETS2
    };

    private const double KmPerMile = 1.609344, LitersPerGallon = 3.785411784, KgPerLb = 0.45359237, BarPerPsi = 0.0689475729;

    public static double Speed(double mph) => Metric ? mph * KmPerMile : mph;
    public static string SpeedUnit => Metric ? "km/h" : "MPH";
    public static string SpeedSpoken => Metric ? "kilometers per hour" : "miles per hour";

    public static double Dist(double miles) => Metric ? miles * KmPerMile : miles;
    public static string DistUnit => Metric ? "km" : "mi";
    public static string DistWord => Metric ? "kilometers" : "miles";
    /// <summary>"426 mi" / "686 km".</summary>
    public static string DistText(double miles, string format = "N0") => $"{Dist(miles).ToString(format, CultureInfo.CurrentCulture)} {DistUnit}";

    public static double Vol(double gallons) => Metric ? gallons * LitersPerGallon : gallons;
    public static string VolUnit => Metric ? "L" : "gal";
    public static string VolWord => Metric ? "liters" : "gallons";

    /// <summary>MPG, or litres per 100 km (lower is better) in metric. 0 stays 0 (unknown).</summary>
    public static double Economy(double mpg) => mpg <= 0 ? 0 : Metric ? 235.214583 / mpg : mpg;
    public static string EconomyUnit => Metric ? "L/100 km" : "MPG";
    public static string EconomySpoken => Metric ? "liters per hundred kilometers" : "miles per gallon";
    public static string EconomyText(double mpg, string format = "F1") =>
        mpg > 0 ? $"{Economy(mpg).ToString(format, CultureInfo.CurrentCulture)} {EconomyUnit}" : $"-- {EconomyUnit}";

    public static string WeightText(double lbs) => Metric
        ? (lbs * KgPerLb >= 1000 ? $"{lbs * KgPerLb / 1000:N1} t" : $"{lbs * KgPerLb:N0} kg")
        : $"{lbs:N0} lbs";

    public static double Temp(double fahrenheit) => Metric ? (fahrenheit - 32) * 5 / 9 : fahrenheit;
    public static string TempText(double fahrenheit) => $"{Temp(fahrenheit):F0}°{(Metric ? "C" : "F")}";

    public static string PressureText(double psi) => Metric ? $"{psi * BarPerPsi:F1} bar" : $"{psi:F0} PSI";

    /// <summary>In-game money: dollars in ATS, euros in ETS2.</summary>
    public static string Money(long amount, uint gameId) =>
        (amount < 0 ? "-" : "") + (gameId == 1 ? "€" : "$") + Math.Abs(amount).ToString("N0", CultureInfo.CurrentCulture);
}
