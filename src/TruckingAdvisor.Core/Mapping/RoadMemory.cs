namespace TruckingAdvisor.Core.Mapping;

/// <summary>
/// The roads you've driven, recorded from the truck's world position. Only new road is stored:
/// driving a road you already know adds nothing, so the file stays small however long you play.
/// Works for every map (base game, DLC regions and map mods) because it comes from telemetry,
/// not from the game's files. World axes: +X east, +Z south.
/// </summary>
public sealed class RoadMemory
{
    /// <summary>A point is stored when the truck has moved this far (world units ≈ meters).</summary>
    public const float Step = 30f;
    /// <summary>Grid used to tell known road from new road.</summary>
    public const float CellSize = 40f;
    /// <summary>Bigger jumps (ferry, train, teleport, loading a save) start a new line.</summary>
    public const float MaxJump = 400f;
    public const int MaxPoints = 2_000_000;

    private readonly List<float[]> _segments = new();       // each: x0,z0,x1,z1,...
    private readonly HashSet<long> _cells = new();
    /// <summary>Cells added by the stretch being recorded now (they don't count as "known road" yet).</summary>
    private readonly HashSet<long> _run = new();
    private List<float>? _current;
    private float _lastX, _lastZ;
    private bool _hasLast;
    private int _points;

    /// <summary>Changes whenever something is added (lets the map redraw only when needed).</summary>
    public int Version { get; private set; }
    public int PointCount => _points;
    public int SegmentCount => _segments.Count + (_current is { Count: >= 4 } ? 1 : 0);

    /// <summary>Approximate miles of distinct road recorded (world units are metres of driving).</summary>
    public double KnownRoadMiles => _points * Step / 1609.344;
    private readonly object _lock = new();

    public float MinX { get; private set; } = float.MaxValue;
    public float MaxX { get; private set; } = float.MinValue;
    public float MinZ { get; private set; } = float.MaxValue;
    public float MaxZ { get; private set; } = float.MinValue;
    public bool IsEmpty => _points == 0;

    /// <summary>Feed the truck's position (only while live and moving).</summary>
    public void Record(double x, double z, bool moving)
    {
        lock (_lock) RecordCore(x, z, moving);
    }

    private void RecordCore(double x, double z, bool moving)
    {
        float fx = (float)x, fz = (float)z;
        if (!moving || (fx == 0 && fz == 0)) { EndRun(); _hasLast = false; return; }
        if (_hasLast)
        {
            float dx = fx - _lastX, dz = fz - _lastZ, d2 = dx * dx + dz * dz;
            if (d2 > MaxJump * MaxJump) { EndRun(); _hasLast = false; }  // teleport / ferry / train
            else if (d2 < Step * Step) return;                          // not far enough yet
        }
        (float X, float Z)? previous = _hasLast ? (_lastX, _lastZ) : null;
        _lastX = fx; _lastZ = fz; _hasLast = true;

        if (IsKnown(fx, fz))
        {
            // Back on road we already have: finish the new piece right here so it joins up.
            if (_current is not null) { _current.Add(fx); _current.Add(fz); Version++; }
            EndRun();
            return;
        }
        long key = Key(fx, fz);
        _cells.Add(key);
        _run.Add(key);
        if (_points >= MaxPoints) return;
        if (_current is null)
        {
            _current = new List<float>(64);
            if (previous is { } p) { _current.Add(p.X); _current.Add(p.Z); } // join from the last point, no gap
        }
        _current.Add(fx); _current.Add(fz);
        Bump(fx, fz);
        if (_current.Count >= 512)
        {
            Flush();
            _current = new List<float>(64) { fx, fz };
        }
    }

    private void Bump(float x, float z)
    {
        _points++;
        Version++;
        if (x < MinX) MinX = x;
        if (x > MaxX) MaxX = x;
        if (z < MinZ) MinZ = z;
        if (z > MaxZ) MaxZ = z;
    }

    /// <summary>Known road = a cell (or its neighbour, for lane changes and the other carriageway) from an earlier drive.</summary>
    private bool IsKnown(float x, float z)
    {
        int cx = (int)MathF.Floor(x / CellSize), cz = (int)MathF.Floor(z / CellSize);
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                long k = Pack(cx + dx, cz + dz);
                if (_cells.Contains(k) && !_run.Contains(k)) return true;
            }
        return false;
    }

    private void EndRun()
    {
        Flush();
        _run.Clear();
    }

    private void Flush()
    {
        if (_current is { Count: >= 4 }) _segments.Add(_current.ToArray());
        _current = null;
    }

    /// <summary>All lines, including the one being drawn right now.</summary>
    public IReadOnlyList<float[]> Segments()
    {
        lock (_lock) return SegmentsCore();
    }

    private IReadOnlyList<float[]> SegmentsCore()
    {
        if (_current is not { Count: >= 4 }) return _segments.ToList();
        var all = new List<float[]>(_segments.Count + 1);
        all.AddRange(_segments);
        all.Add(_current.ToArray());
        return all;
    }

    private static long Key(float x, float z) => Pack((int)MathF.Floor(x / CellSize), (int)MathF.Floor(z / CellSize));
    private static long Pack(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

    // ---- Storage: small binary file ----

    private const int Magic = 0x31444F52; // "ROD1"

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                var segs = Segments();
                w.Write(Magic);
                w.Write(segs.Count);
                foreach (var s in segs)
                {
                    w.Write(s.Length);
                    foreach (var v in s) w.Write(v);
                }
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best effort */ }
    }

    public static RoadMemory Load(string path)
    {
        var m = new RoadMemory();
        try
        {
            if (!File.Exists(path)) return m;
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != Magic) return m;
            int count = r.ReadInt32();
            for (int i = 0; i < count && m._points < MaxPoints; i++)
            {
                int len = r.ReadInt32();
                if (len < 4 || len % 2 != 0 || len > 1_000_000) break;
                var seg = new float[len];
                for (int j = 0; j < len; j++) seg[j] = r.ReadSingle();
                m._segments.Add(seg);
                for (int j = 0; j < len; j += 2)
                {
                    m._cells.Add(Key(seg[j], seg[j + 1]));
                    m.Bump(seg[j], seg[j + 1]);
                }
            }
        }
        catch { /* a damaged file keeps what was read */ }
        return m;
    }
}

/// <summary>Everything learned about one game's world. ATS and ETS2 use overlapping coordinates, so each has its own.</summary>
public sealed class GameWorld
{
    public uint GameId { get; }
    public string Key => GameId == 1 ? "ets2" : "ats";
    public LocationMemory Cities { get; }
    public GradeMemory Grades { get; }
    public RoadMemory Roads { get; }
    private readonly string _dir;

    private GameWorld(uint gameId, string dir, LocationMemory cities, GradeMemory grades, RoadMemory roads)
    {
        GameId = gameId; _dir = dir; Cities = cities; Grades = grades; Roads = roads;
    }

    /// <summary>
    /// Loads one game's memories from <paramref name="dir"/>. Files from versions before per-game
    /// storage (learned-cities.json / learned-grades.json) were ATS-only and become ATS's.
    /// </summary>
    public static GameWorld Load(string dir, uint gameId)
    {
        string key = gameId == 1 ? "ets2" : "ats";
        string cities = Path.Combine(dir, $"learned-cities-{key}.json"), grades = Path.Combine(dir, $"learned-grades-{key}.json");
        if (key == "ats")
        {
            TryMigrate(Path.Combine(dir, "learned-cities.json"), cities);
            TryMigrate(Path.Combine(dir, "learned-grades.json"), grades);
        }
        return new GameWorld(gameId == 1 ? 1u : 2u, dir, LocationMemory.Load(cities), GradeMemory.Load(grades),
            RoadMemory.Load(Path.Combine(dir, $"driven-roads-{key}.bin")));
    }

    public void Save()
    {
        Cities.Save(Path.Combine(_dir, $"learned-cities-{Key}.json"));
        Grades.Save(Path.Combine(_dir, $"learned-grades-{Key}.json"));
        Roads.Save(Path.Combine(_dir, $"driven-roads-{Key}.bin"));
    }

    private static void TryMigrate(string oldPath, string newPath)
    {
        try { if (File.Exists(oldPath) && !File.Exists(newPath)) File.Move(oldPath, newPath); } catch { }
    }
}
