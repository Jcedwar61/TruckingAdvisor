using System.Text.Json;

namespace TruckingAdvisor.Core.Mapping;

/// <summary>
/// Learns where cities are from your own deliveries: the truck's position when a job starts is in the
/// origin city, and when a delivery completes it's in the destination city. Later, the nearest learned
/// city gives an approximate location. Only places you've actually been are known.
/// </summary>
public sealed class LocationMemory
{
    /// <summary>Within this distance (game world units) you're "in" the city.</summary>
    public const double InCityRadius = 2500;
    /// <summary>Within this distance you're "near" it; beyond it the location is unknown.</summary>
    public const double NearRadius = 15000;

    public sealed class City
    {
        public string Name { get; set; } = "";
        public double X { get; set; }
        public double Z { get; set; }
        public int Samples { get; set; }
    }

    public Dictionary<string, City> Cities { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a sighting of a city at a world position (averages repeat visits; ignores outliers).</summary>
    public void Learn(string city, double x, double z)
    {
        if (string.IsNullOrWhiteSpace(city) || (x == 0 && z == 0)) return;
        if (!Cities.TryGetValue(city, out var c))
        {
            Cities[city] = new City { Name = city, X = x, Z = z, Samples = 1 };
            return;
        }
        // A company far from the learned center is probably a mismatch (e.g. same name elsewhere): skip it.
        if (Distance(c.X, c.Z, x, z) > NearRadius) return;
        int n = Math.Min(c.Samples, 20); // keep adapting a little
        c.X = (c.X * n + x) / (n + 1);
        c.Z = (c.Z * n + z) / (n + 1);
        c.Samples++;
    }

    /// <summary>"in Denver, CO" / "near Denver, CO" / null when nothing learned is close.</summary>
    public string? Describe(double x, double z)
    {
        if (Cities.Count == 0 || (x == 0 && z == 0)) return null;
        var best = Cities.Values.Select(c => (City: c, D: Distance(c.X, c.Z, x, z))).MinBy(t => t.D);
        if (best.D > NearRadius) return null;
        string state = CityStates.StateFor(best.City.Name) is { } st ? $", {st}" : "";
        return $"{(best.D <= InCityRadius ? "in" : "near")} {best.City.Name}{state}";
    }

    private static double Distance(double x1, double z1, double x2, double z2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (z1 - z2) * (z1 - z2));

    public static LocationMemory Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<LocationMemory>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch { /* best effort */ }
    }
}
