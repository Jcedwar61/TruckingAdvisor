using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

namespace TruckingAdvisor.Core.Mapping;

/// <summary>
/// Real-world places for the regions the games cover: towns, states / provinces / countries and
/// numbered highways from Natural Earth (public domain), bundled with the app. Coordinates are
/// projected to kilometres on a Lambert conformal conic projection suited to each region.
/// </summary>
public sealed class Gazetteer
{
    public sealed record Town(string Name, string[] AltNames, string Admin, string Country, double Lat, double Lon, int Population, Vector2 Km);
    public sealed record Area(string Name, string Country, string Abbr, IReadOnlyList<Vector2[]> Rings, Vector4 Box);
    public sealed record Road(string Name, Vector2[] Points, Vector4 Box);

    public string Region { get; }
    public IReadOnlyList<Town> Towns { get; }
    public IReadOnlyList<Area> Areas { get; }
    public IReadOnlyList<Road> Roads { get; }
    private readonly Dictionary<string, List<Town>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lcc _proj;

    private Gazetteer(string region, Lcc proj, List<Town> towns, List<Area> areas, List<Road> roads)
    {
        Region = region; _proj = proj; Towns = towns; Areas = areas; Roads = roads;
        foreach (var t in towns)
            foreach (var n in t.AltNames.Prepend(t.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_byName.TryGetValue(n, out var list)) _byName[n] = list = new List<Town>();
                list.Add(t);
            }
    }

    /// <summary>Real towns with this name (game city names are matched against names and alternate names).</summary>
    public IReadOnlyList<Town> FindTowns(string name) =>
        _byName.TryGetValue(name.Trim(), out var list) ? list : Array.Empty<Town>();

    public Vector2 Project(double lat, double lon) => _proj.Forward(lat, lon);

    private static readonly Dictionary<string, Gazetteer> Cache = new();

    /// <summary>"ats" (USA, Canada, Mexico) or "ets2" (Europe). Loaded once from the bundled data.</summary>
    public static Gazetteer For(string region)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(region, out var g)) return g;
            g = Load(region);
            Cache[region] = g;
            return g;
        }
    }

    private static Gazetteer Load(string region)
    {
        var proj = region == "ets2" ? new Lcc(43, 62, 52, 10) : new Lcc(33, 45, 39, -96);
        var towns = new List<Town>();
        var areas = new List<Area>();
        var roads = new List<Road>();
        using var stream = typeof(Gazetteer).Assembly.GetManifestResourceStream("TruckingAdvisor.Core.Mapping.Data.geo.json.gz");
        if (stream is null) return new Gazetteer(region, proj, towns, areas, roads);
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gz);
        var r = doc.RootElement.GetProperty("regions").GetProperty(region);

        foreach (var t in r.GetProperty("towns").EnumerateArray())
        {
            double lat = t.GetProperty("lat").GetDouble(), lon = t.GetProperty("lon").GetDouble();
            towns.Add(new Town(t.GetProperty("n").GetString() ?? "", t.GetProperty("alt").EnumerateArray().Select(a => a.GetString() ?? "").ToArray(),
                t.GetProperty("a").GetString() ?? "", t.GetProperty("c").GetString() ?? "", lat, lon, t.GetProperty("pop").GetInt32(), proj.Forward(lat, lon)));
        }
        foreach (var a in r.GetProperty("areas").EnumerateArray())
        {
            var rings = a.GetProperty("r").EnumerateArray().Select(ring => ring.EnumerateArray()
                .Select(p => proj.Forward(p[1].GetDouble(), p[0].GetDouble())).ToArray()).ToList();
            areas.Add(new Area(a.GetProperty("n").GetString() ?? "", a.GetProperty("c").GetString() ?? "", a.GetProperty("abbr").GetString() ?? "",
                rings, Box(rings.SelectMany(x => x))));
        }
        foreach (var road in r.GetProperty("roads").EnumerateArray())
        {
            var pts = road.GetProperty("p").EnumerateArray().Select(p => proj.Forward(p[1].GetDouble(), p[0].GetDouble())).ToArray();
            roads.Add(new Road(road.GetProperty("n").GetString() ?? "", pts, Box(pts)));
        }
        return new Gazetteer(region, proj, towns, areas, roads);
    }

    private static Vector4 Box(IEnumerable<Vector2> pts)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var p in pts) { x0 = MathF.Min(x0, p.X); y0 = MathF.Min(y0, p.Y); x1 = MathF.Max(x1, p.X); y1 = MathF.Max(y1, p.Y); }
        return new Vector4(x0, y0, x1, y1);
    }

    /// <summary>Lambert conformal conic on a sphere; output in km, +X east, +Y north.</summary>
    private sealed class Lcc
    {
        private const double R = 6371.0;
        private readonly double _n, _f, _rho0, _lon0;

        public Lcc(double lat1, double lat2, double lat0, double lon0)
        {
            double p1 = Rad(lat1), p2 = Rad(lat2);
            _n = Math.Log(Math.Cos(p1) / Math.Cos(p2)) / Math.Log(Math.Tan(Math.PI / 4 + p2 / 2) / Math.Tan(Math.PI / 4 + p1 / 2));
            _f = Math.Cos(p1) * Math.Pow(Math.Tan(Math.PI / 4 + p1 / 2), _n) / _n;
            _rho0 = R * _f / Math.Pow(Math.Tan(Math.PI / 4 + Rad(lat0) / 2), _n);
            _lon0 = lon0;
        }

        public Vector2 Forward(double lat, double lon)
        {
            double rho = R * _f / Math.Pow(Math.Tan(Math.PI / 4 + Rad(lat) / 2), _n);
            double theta = _n * Rad(lon - _lon0);
            return new Vector2((float)(rho * Math.Sin(theta)), (float)(_rho0 - rho * Math.Cos(theta)));
        }

        private static double Rad(double d) => d * Math.PI / 180;
    }
}

/// <summary>
/// Ties game coordinates to the real world, separately for each game, using cities the player has
/// actually been to (learned at pickups and deliveries) whose names match real towns.
/// Model: game = a·real + b (scale, rotation and offset as one complex number pair), fitted robustly,
/// then nudged so every matched city lands exactly on its learned spot.
/// Needs at least two matched cities; more cities = better accuracy.
/// </summary>
public sealed class GeoCalibration
{
    public sealed record Match(string GameName, Vector2 Game, Gazetteer.Town Town, Vector2 Residual);

    private Complex _a, _b;
    private readonly List<Match> _matches = new();
    public IReadOnlyList<Match> Matches => _matches;
    public bool IsReady { get; private set; }
    /// <summary>"rough" with 2 matched cities, "good" with 3 or more that agree.</summary>
    public string Quality => !IsReady ? "none" : _matches.Count >= 3 ? "good" : "rough";
    /// <summary>Game world units (metres of driving) per real kilometre.</summary>
    public double Scale => _a.Magnitude;

    // Map scale is about 1:19-20 in both games, and north is up; reject fits that disagree badly.
    private const double MinScale = 15, MaxScale = 150, MaxRotationDeg = 30, InlierGameUnits = 4000;

    /// <summary>Game (x, z) with +z south becomes a north-up complex number x - i·z.</summary>
    private static Complex G(double x, double z) => new(x, -z);
    private static Complex W(Vector2 km) => new(km.X, km.Y);

    public static GeoCalibration Fit(Gazetteer gaz, IEnumerable<LocationMemory.City> learned, Func<string, string?>? stateFor = null)
    {
        var cal = new GeoCalibration();
        var cands = new List<(LocationMemory.City City, IReadOnlyList<Gazetteer.Town> Towns)>();
        foreach (var c in learned)
        {
            var towns = gaz.FindTowns(c.Name);
            // ATS: when the game city's state is known, only accept towns in that state.
            if (stateFor?.Invoke(c.Name) is { Length: > 0 } st)
            {
                var abbrToName = gaz.Areas.Where(a => a.Abbr.Length > 0).GroupBy(a => a.Abbr).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
                string stateName = abbrToName.TryGetValue(st, out var full) ? full : st;
                towns = towns.Where(t => string.Equals(t.Admin, stateName, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (towns.Count > 0 && towns.Count <= 8) cands.Add((c, towns));
        }
        if (cands.Count < 2) return cal;

        // Try every pair of cities (and their candidate towns), keep the fit most cities agree with.
        int bestInliers = 0; double bestErr = double.MaxValue; Complex bestA = default, bestB = default;
        int pairs = 0;
        for (int i = 0; i < cands.Count && pairs < 5000; i++)
            for (int j = i + 1; j < cands.Count && pairs < 5000; j++)
                foreach (var ti in cands[i].Towns)
                    foreach (var tj in cands[j].Towns)
                    {
                        pairs++;
                        var g1 = G(cands[i].City.X, cands[i].City.Z); var g2 = G(cands[j].City.X, cands[j].City.Z);
                        var w1 = W(ti.Km); var w2 = W(tj.Km);
                        if ((w2 - w1).Magnitude < 1) continue;
                        var a = (g2 - g1) / (w2 - w1);
                        if (!Plausible(a)) continue;
                        var b = g1 - a * w1;
                        int inliers = 0; double err = 0;
                        foreach (var c in cands)
                        {
                            var gc = G(c.City.X, c.City.Z);
                            double best = c.Towns.Min(t => (a * W(t.Km) + b - gc).Magnitude);
                            if (best < InlierGameUnits) { inliers++; err += best; }
                        }
                        if (inliers > bestInliers || (inliers == bestInliers && err < bestErr))
                        { bestInliers = inliers; bestErr = err; bestA = a; bestB = b; }
                    }
        if (bestInliers < 2) return cal;

        // Least-squares refit on the cities that agree.
        var used = new List<(LocationMemory.City City, Gazetteer.Town Town)>();
        foreach (var c in cands)
        {
            var gc = G(c.City.X, c.City.Z);
            var t = c.Towns.MinBy(t => (bestA * W(t.Km) + bestB - gc).Magnitude)!;
            if ((bestA * W(t.Km) + bestB - gc).Magnitude < InlierGameUnits) used.Add((c.City, t));
        }
        Complex wm = Mean(used.Select(u => W(u.Town.Km))), gm = Mean(used.Select(u => G(u.City.X, u.City.Z)));
        Complex num = 0; double den = 0;
        foreach (var u in used)
        {
            var dw = W(u.Town.Km) - wm;
            num += (G(u.City.X, u.City.Z) - gm) * Complex.Conjugate(dw);
            den += dw.Magnitude * dw.Magnitude;
        }
        var fa = den > 0 ? num / den : bestA;
        if (!Plausible(fa)) fa = bestA;
        cal._a = fa;
        cal._b = gm - fa * wm;
        foreach (var u in used)
        {
            var g = G(u.City.X, u.City.Z);
            var res = g - (cal._a * W(u.Town.Km) + cal._b);
            cal._matches.Add(new Match(u.City.Name, new Vector2((float)g.Real, (float)g.Imaginary), u.Town, new Vector2((float)res.Real, (float)res.Imaginary)));
        }
        cal.IsReady = true;
        return cal;
    }

    private static bool Plausible(Complex a) =>
        a.Magnitude is >= MinScale and <= MaxScale && Math.Abs(a.Phase * 180 / Math.PI) <= MaxRotationDeg;

    private static Complex Mean(IEnumerable<Complex> xs)
    {
        Complex s = 0; int n = 0;
        foreach (var x in xs) { s += x; n++; }
        return n > 0 ? s / n : 0;
    }

    /// <summary>Game position → real-world km (projected), with a local correction so matched cities line up.</summary>
    public Vector2 ToReal(double x, double z)
    {
        var g = G(x, z);
        // Inverse-distance-weighted residuals, applied at half strength: part of a city's residual is real
        // map distortion, part is just where the depot sits in town, so don't trust it fully.
        Complex corr = 0; double wsum = 0;
        foreach (var m in _matches)
        {
            double d = (g - new Complex(m.Game.X, m.Game.Y)).Magnitude;
            double w = 1 / Math.Pow(Math.Max(d, 50), 2);
            corr += w * new Complex(m.Residual.X, m.Residual.Y);
            wsum += w;
        }
        if (wsum > 0) g -= 0.5 * corr / wsum;
        var real = (g - _b) / _a;
        return new Vector2((float)real.Real, (float)real.Imaginary);
    }

    /// <summary>Real-world km → game position (x, z).</summary>
    public (double X, double Z) ToGame(Vector2 km)
    {
        var g = _a * W(km) + _b;
        return (g.Real, -g.Imaginary);
    }
}

/// <summary>Where the truck is, in real-world terms, for the AI and the header.</summary>
public sealed record PlaceDescription(string Text, string? Area, string? AreaAbbr, string? NearTown, string? Highway, string? Toward, string Quality);

/// <summary>Turns a calibrated game position into "east of Flagstaff, Arizona, on or near I-40, heading east toward Winslow".</summary>
public static class PlaceDescriber
{
    private static readonly string[] Compass = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };

    public static string Direction(Vector2 from, Vector2 to)
    {
        double deg = (Math.Atan2(to.X - from.X, to.Y - from.Y) * 180 / Math.PI + 360) % 360;
        return Compass[(int)Math.Round(deg / 45) % 8];
    }

    public static PlaceDescription? Describe(Gazetteer gaz, GeoCalibration cal, double x, double z, float headingDeg, bool moving,
        IReadOnlyCollection<string>? gameCityNames = null)
    {
        if (!cal.IsReady || (x == 0 && z == 0)) return null;
        var p = cal.ToReal(x, z);

        // State / province / country.
        var area = gaz.Areas.FirstOrDefault(a => InBox(a.Box, p, 0) && a.Rings.Any(r => Contains(r, p)));

        // Nearest town: prefer cities the player has been to (they exist in the game) and bigger towns.
        var known = gameCityNames is null ? null : new HashSet<string>(gameCityNames, StringComparer.OrdinalIgnoreCase);
        bool InGame(Gazetteer.Town t) => known is not null && (known.Contains(t.Name) || t.AltNames.Any(known.Contains));
        string DisplayName(Gazetteer.Town t) => known is null ? t.Name : t.AltNames.Prepend(t.Name).FirstOrDefault(known.Contains) ?? t.Name;

        var near = gaz.Towns
            .Where(t => InGame(t) || t.Population >= 15000)
            .Select(t => (Town: t, Km: Vector2.Distance(t.Km, p)))
            .Where(t => t.Km < 120)
            .MinBy(t => t.Km * (InGame(t.Town) ? 0.7 : 1.0));

        // Highway: a numbered road close by that runs the way the truck is going.
        string? highway = null;
        double hx = Math.Sin(headingDeg * Math.PI / 180), hy = Math.Cos(headingDeg * Math.PI / 180);
        double bestRoad = 12.0; // km (real world): the game map isn't exact, the heading check keeps it honest
        foreach (var road in gaz.Roads)
        {
            if (!InBox(road.Box, p, 12)) continue;
            for (int i = 1; i < road.Points.Length; i++)
            {
                var a = road.Points[i - 1]; var b = road.Points[i];
                double d = SegmentDistance(p, a, b);
                if (d >= bestRoad) continue;
                if (moving)
                {
                    var seg = Vector2.Normalize(b - a);
                    if (float.IsNaN(seg.X) || Math.Abs(seg.X * hx + seg.Y * hy) < 0.8) continue; // within ~37° either way
                }
                bestRoad = d;
                highway = road.Name;
            }
        }

        // Next town ahead (only while driving).
        string? toward = null;
        if (moving)
        {
            var ahead = gaz.Towns
                .Where(t => InGame(t) || t.Population >= 8000)
                .Select(t => (Town: t, V: t.Km - p))
                .Select(t => (t.Town, t.V, Km: t.V.Length()))
                .Where(t => t.Km is > 12 and < 300 && (t.V.X * hx + t.V.Y * hy) / t.Km > 0.82)
                .OrderBy(t => t.Km * (InGame(t.Town) ? 0.7 : 1.0))
                .FirstOrDefault();
            if (ahead.Town is not null && (near.Town is null || ahead.Town != near.Town)) toward = DisplayName(ahead.Town);
        }

        string? areaName = area?.Name;
        string where;
        if (near.Town is null) where = areaName is null ? "somewhere on the road" : $"somewhere in {areaName}";
        else
        {
            string town = DisplayName(near.Town);
            string inArea = areaName is not null ? $", {areaName}" : near.Town.Admin.Length > 0 ? $", {near.Town.Admin}" : "";
            where = near.Km switch
            {
                < 6 => $"in {town}{inArea}",
                < 20 => $"just outside {town}{inArea}",
                _ => $"{Direction(near.Town.Km, p)} of {town}{inArea}"
            };
        }
        var parts = new List<string> { where };
        if (highway is not null) parts.Add($"on or near {highway}");
        if (toward is not null) parts.Add($"heading {Compass[(int)Math.Round(((headingDeg % 360) + 360) % 360 / 45) % 8]} toward {toward}");
        return new PlaceDescription(string.Join(", ", parts), areaName, area?.Abbr, near.Town is null ? null : DisplayName(near.Town), highway, toward, cal.Quality);
    }

    private static bool InBox(Vector4 box, Vector2 p, float pad) =>
        p.X >= box.X - pad && p.X <= box.Z + pad && p.Y >= box.Y - pad && p.Y <= box.W + pad;

    private static bool Contains(Vector2[] ring, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y) &&
                p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        }
        return inside;
    }

    private static double SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1) : 0;
        return Vector2.Distance(p, a + t * ab);
    }
}
