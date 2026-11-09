using System.Text.Json;

namespace TruckingAdvisor.Core.Mapping;

/// <summary>
/// Remembers steep downgrades you've driven (position, direction, grade) and warns when you're heading
/// toward one again. It only knows roads you've driven before; it is not a map of the game.
/// </summary>
public sealed class GradeMemory
{
    public const double CellSize = 100;          // world units per grid cell
    public const float RecordAtOrBelow = -4f;    // grade % worth remembering
    public const float WarnAtOrBelow = -5f;      // grade % worth warning about
    public const int MaxCells = 200_000;

    public sealed class Cell
    {
        public float Grade { get; set; }         // steepest downhill seen (negative %)
        public float Heading { get; set; }       // compass bearing when driven downhill
    }

    /// <summary>Key "x:z" of grid cells.</summary>
    public Dictionary<string, Cell> Cells { get; set; } = new();

    private static string Key(double x, double z) => $"{(int)Math.Floor(x / CellSize)}:{(int)Math.Floor(z / CellSize)}";

    /// <summary>Call while moving with the measured grade and compass heading (0 = north, 90 = east).</summary>
    public void Record(double x, double z, float gradePercent, float headingDegrees, float speedMph)
    {
        if (speedMph < 10 || gradePercent > RecordAtOrBelow || Cells.Count >= MaxCells) return;
        string key = Key(x, z);
        if (!Cells.TryGetValue(key, out var c) || gradePercent < c.Grade)
            Cells[key] = new Cell { Grade = gradePercent, Heading = headingDegrees };
    }

    /// <summary>
    /// Looks 300-2000 units ahead along the heading for a recorded steep downgrade driven in the same
    /// direction. Returns the steepest grade found and its distance, or null.
    /// SCS world: +X is east, -Z is north.
    /// </summary>
    public (float Grade, double Distance)? Ahead(double x, double z, float headingDegrees)
    {
        if (Cells.Count == 0) return null;
        double rad = headingDegrees * Math.PI / 180.0;
        double dx = Math.Sin(rad), dz = -Math.Cos(rad);
        (float Grade, double Distance)? worst = null;
        for (double d = 300; d <= 2000; d += CellSize / 2)
        {
            if (!Cells.TryGetValue(Key(x + dx * d, z + dz * d), out var c)) continue;
            if (c.Grade > WarnAtOrBelow || AngleDiff(c.Heading, headingDegrees) > 45) continue;
            if (worst is null || c.Grade < worst.Value.Grade) worst = (c.Grade, d);
        }
        return worst;
    }

    private static double AngleDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    public static GradeMemory Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<GradeMemory>(File.ReadAllText(path)) ?? new() : new(); }
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
