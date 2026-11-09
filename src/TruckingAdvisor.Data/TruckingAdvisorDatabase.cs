using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Data;

/// <summary>
/// Local SQLite storage in %LOCALAPPDATA%\TruckingAdvisor\truckingadvisor.db.
/// Each record is stored as JSON next to a few indexed columns, which keeps the schema
/// stable while the models evolve. Everything stays on this PC.
/// </summary>
public sealed class TruckingAdvisorDatabase
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _connectionString;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckingAdvisor", "truckingadvisor.db");

    public TruckingAdvisorDatabase(string? dbPath = null)
    {
        dbPath ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS DeliveryTrips (
                Id TEXT PRIMARY KEY, StartedAt TEXT NOT NULL, State INTEGER NOT NULL,
                DistanceMiles REAL NOT NULL, FinalScore INTEGER NOT NULL, Json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Trips_StartedAt ON DeliveryTrips(StartedAt);
            CREATE TABLE IF NOT EXISTS Truckers (Id TEXT PRIMARY KEY, Handle TEXT NOT NULL, Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Company (Id INTEGER PRIMARY KEY CHECK (Id = 1), Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Career (Id INTEGER PRIMARY KEY CHECK (Id = 1), Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ServiceHistory (Id TEXT PRIMARY KEY, ServiceDate TEXT NOT NULL, Json TEXT NOT NULL);
            """);
        Exec(c, "INSERT OR REPLACE INTO Meta(Key, Value) VALUES ('SchemaVersion', $v)", ("$v", SchemaVersion.ToString(CultureInfo.InvariantCulture)));
    }

    // ---- Trips ----

    public void SaveTrip(DeliveryTripRecord trip) => Upsert(
        "INSERT OR REPLACE INTO DeliveryTrips(Id, StartedAt, State, DistanceMiles, FinalScore, Json) VALUES ($id, $s, $st, $d, $f, $j)",
        ("$id", trip.Id.ToString()), ("$s", trip.StartedAt.ToString("O")), ("$st", (int)trip.State),
        ("$d", (double)trip.DistanceMiles), ("$f", trip.ScoreBreakdown.FinalScore), ("$j", JsonSerializer.Serialize(trip, Json)));

    public List<DeliveryTripRecord> LoadTrips(int limit = 500) =>
        Query<DeliveryTripRecord>("SELECT Json FROM DeliveryTrips ORDER BY StartedAt DESC LIMIT $n", ("$n", limit));

    // ---- Truckers (relationships & memories) ----

    public void SaveTrucker(TruckerPersonality t) => Upsert(
        "INSERT OR REPLACE INTO Truckers(Id, Handle, Json) VALUES ($id, $h, $j)",
        ("$id", t.Id), ("$h", t.Handle), ("$j", JsonSerializer.Serialize(t, Json)));

    public List<TruckerPersonality> LoadTruckers() => Query<TruckerPersonality>("SELECT Json FROM Truckers");

    /// <summary>Keeps catalog identities but restores saved relationship progress and memories.</summary>
    public List<TruckerPersonality> MergeWithSaved(List<TruckerPersonality> catalog)
    {
        var saved = LoadTruckers().ToDictionary(t => t.Id);
        foreach (var t in catalog)
        {
            if (!saved.TryGetValue(t.Id, out var s)) continue;
            t.Relationship = s.Relationship;
            t.RelationshipPoints = s.RelationshipPoints;
            t.ConversationCount = s.ConversationCount;
            t.LastSpokenAt = s.LastSpokenAt;
            t.SummarizedMemories = s.SummarizedMemories;
        }
        var known = catalog.Select(t => t.Id).ToHashSet();
        catalog.AddRange(saved.Values.Where(s => !known.Contains(s.Id))); // dynamically generated truckers
        return catalog;
    }

    // ---- Company / career / service ----

    public void SaveCompany(VirtualCompanyProfile p) => Upsert("INSERT OR REPLACE INTO Company(Id, Json) VALUES (1, $j)", ("$j", JsonSerializer.Serialize(p, Json)));
    public VirtualCompanyProfile LoadCompany() => Query<VirtualCompanyProfile>("SELECT Json FROM Company WHERE Id = 1").FirstOrDefault() ?? new VirtualCompanyProfile();

    public void SaveCareer(CareerProfile p) => Upsert("INSERT OR REPLACE INTO Career(Id, Json) VALUES (1, $j)", ("$j", JsonSerializer.Serialize(p, Json)));
    public CareerProfile LoadCareer() => Query<CareerProfile>("SELECT Json FROM Career WHERE Id = 1").FirstOrDefault() ?? new CareerProfile();

    private List<ServiceRecord>? _serviceCache;

    /// <summary>Newest first; cached because the advisor asks every second.</summary>
    public List<ServiceRecord> LoadServiceRecordsCached() => _serviceCache ??= LoadServiceRecords();

    public void SaveServiceRecord(ServiceRecord r)
    {
        _serviceCache = null;
        SaveServiceRecordCore(r);
    }

    private void SaveServiceRecordCore(ServiceRecord r) => Upsert(
        "INSERT OR REPLACE INTO ServiceHistory(Id, ServiceDate, Json) VALUES ($id, $d, $j)",
        ("$id", r.Id.ToString()), ("$d", r.ServiceDate.ToString("O")), ("$j", JsonSerializer.Serialize(r, Json)));
    public List<ServiceRecord> LoadServiceRecords() => Query<ServiceRecord>("SELECT Json FROM ServiceHistory ORDER BY ServiceDate DESC");

    // ---- Exports ----

    public static string ExportDeliveryReportText(DeliveryTripRecord trip)
    {
        var inv = CultureInfo.InvariantCulture;
        string mid = trip.StartedMidDelivery ? "\nNote: TruckingAdvisor started mid-delivery; stats cover only the part it saw." : string.Empty;
        string mpg = trip.AverageMpg > 0 ? trip.AverageMpg.ToString("F1", inv) + " MPG" : "n/a";
        return string.Create(inv, $"""
TRUCKINGADVISOR - DELIVERY REPORT
=================================================
Cargo: {trip.Cargo} ({trip.CargoWeightLbs:N0} lbs)
Origin: {trip.Origin} -> Destination: {trip.Destination}
Distance: {trip.DistanceMiles:F0} Miles | Driving Time: {(int)trip.DrivingDuration.TotalHours}h {trip.DrivingDuration.Minutes:D2}m
Avg Speed: {trip.AverageSpeedMph:F0} MPH | Max Speed: {trip.MaxSpeedMph:F0} MPH
Fuel: {trip.FuelUsedGallons:F0} Gallons | Economy: {mpg}
Speeding Incidents: {trip.SpeedingIncidents} | Harsh Braking: {trip.HarshBrakingIncidents}
Truck Damage: {trip.AddedTruckDamagePercent:F1}% | Trailer Damage: {trip.AddedTrailerDamagePercent:F1}%

FINAL DRIVER SCORE: {trip.ScoreBreakdown.FinalScore}/100
RATING: {trip.ScoreBreakdown.RatingLabel.ToUpperInvariant()}{mid}
""");
    }

    public static string ExportDeliveryReportJson(DeliveryTripRecord trip) =>
        JsonSerializer.Serialize(trip, new JsonSerializerOptions { WriteIndented = true });

    public static string ExportDeliveryReportCsv(IEnumerable<DeliveryTripRecord> trips)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("TripId,Date,Cargo,Origin,Destination,DistanceMiles,AvgMpg,Speeding,HarshBraking,TruckDamagePct,TrailerDamagePct,Score,Rating\r\n");
        foreach (var t in trips)
        {
            sb.AppendJoin(',',
                Csv(t.Id.ToString()), Csv(t.StartedAt.ToString("yyyy-MM-dd", inv)), Csv(t.Cargo), Csv(t.Origin), Csv(t.Destination),
                t.DistanceMiles.ToString("F1", inv), t.AverageMpg.ToString("F2", inv),
                t.SpeedingIncidents.ToString(inv), t.HarshBrakingIncidents.ToString(inv),
                t.AddedTruckDamagePercent.ToString("F1", inv), t.AddedTrailerDamagePercent.ToString("F1", inv),
                t.ScoreBreakdown.FinalScore.ToString(inv), Csv(t.ScoreBreakdown.RatingLabel));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>RFC 4180 quoting; also neutralizes spreadsheet formula injection.</summary>
    public static string Csv(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // ---- helpers ----

    private void Upsert(string sql, params (string Name, object Value)[] args)
    {
        using var c = Open();
        Exec(c, sql, args);
    }

    private List<T> Query<T>(string sql, params (string Name, object Value)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var list = new List<T>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            try
            {
                if (JsonSerializer.Deserialize<T>(r.GetString(0), Json) is { } item) list.Add(item);
            }
            catch (JsonException) { /* skip a corrupt row rather than lose everything */ }
        }
        return list;
    }

    private static void Exec(SqliteConnection c, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }
}
