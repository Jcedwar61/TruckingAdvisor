using TruckingAdvisor.Core.Mapping;
using TruckingAdvisor.Shared;

namespace TruckingAdvisor.Presentation;

/// <summary>A city on the map (learned from your deliveries).</summary>
public sealed record MapCity(string Name, double X, double Z);

/// <summary>Everything the map draws, for one game. Immutable: the map redraws roads only when RoadVersion changes.</summary>
public sealed record MapSnapshot(
    string Game,
    IReadOnlyList<float[]> Roads,
    int RoadVersion,
    IReadOnlyList<MapCity> Cities,
    bool HasTruck,
    double TruckX,
    double TruckZ,
    double Heading,
    MapCity? Destination,
    string Summary,
    IReadOnlyList<float[]>? RealRoads = null,
    IReadOnlyList<MapTown>? RealTowns = null,
    int RealVersion = 0);

/// <summary>A real-world town placed on the game map by the calibration (estimated position).</summary>
public sealed record MapTown(string Name, double X, double Z, int Population);

/// <summary>The self-building map: roads you've driven, cities you've delivered to, and where you are now.</summary>
public sealed partial class MainDashboardViewModel
{
    private MapSnapshot? _map;
    private int _mapRoadVersion = -1;
    private IReadOnlyList<float[]> _mapRoads = Array.Empty<float[]>();
    private GeoCalibration? _realFor;
    private IReadOnlyList<float[]>? _realRoads;
    private IReadOnlyList<MapTown>? _realTowns;
    private int _realVersion;

    /// <summary>Real highways and towns moved into game coordinates (recomputed when the calibration changes).</summary>
    private void UpdateRealLayer(GameWorld world)
    {
        var cal = world.Calibration;
        if (ReferenceEquals(cal, _realFor)) return;
        _realFor = cal;
        _realVersion++;
        if (!cal.IsReady) { _realRoads = null; _realTowns = null; return; }
        var gaz = world.Gazetteer;
        _realRoads = gaz.Roads.Select(r =>
        {
            var a = new float[r.Points.Length * 2];
            for (int i = 0; i < r.Points.Length; i++)
            {
                var (x, z) = cal.ToGame(r.Points[i]);
                a[2 * i] = (float)x; a[2 * i + 1] = (float)z;
            }
            return a;
        }).ToList();
        _realTowns = gaz.Towns.Where(t => t.Population >= 10000).Select(t =>
        {
            var (x, z) = cal.ToGame(t.Km);
            return new MapTown(t.Name, x, z, t.Population);
        }).ToList();
    }

    /// <summary>Rebuilt on each UI refresh; cheap unless new road was recorded.</summary>
    public MapSnapshot Map
    {
        get
        {
            var s = _current;
            var world = World;
            var roads = world.Roads;
            if (roads.Version != _mapRoadVersion || _map?.Game != world.Key)
            {
                _mapRoads = roads.Segments();
                _mapRoadVersion = roads.Version;
            }
            var learned = world.Cities.Snapshot();
            var cities = learned.Select(c => new MapCity(c.Name, c.X, c.Z)).ToList();
            MapCity? dest = null;
            if (!string.IsNullOrEmpty(s.DestinationCity) && learned.FirstOrDefault(c => string.Equals(c.Name, s.DestinationCity, StringComparison.OrdinalIgnoreCase)) is { } d)
                dest = new MapCity(d.Name, d.X, d.Z);
            bool truck = (s.IsLive || s.IsDemoData) && !(s.WorldX == 0 && s.WorldZ == 0);
            string game = world.GameId == 1 ? "Euro Truck Simulator 2" : "American Truck Simulator";
            string summary = roads.IsEmpty
                ? $"{game}: no roads recorded yet. Drive and the map draws itself."
                : $"{game}: {Units.DistText(roads.KnownRoadMiles)} of road driven, {cities.Count} cities learned";
            if (!world.IsDemo) UpdateRealLayer(world); else { _realRoads = null; _realTowns = null; }
            if (_realRoads is not null) summary += $" · real highways and towns shown faintly ({world.Calibration.Quality} match)";
            else if (!world.IsDemo && cities.Count < 2) summary += " · real highways and towns appear after deliveries in 2 cities";
            _map = new MapSnapshot(world.Key, _mapRoads, _mapRoadVersion, cities, truck, s.WorldX, s.WorldZ, s.HeadingDegrees, dest, summary,
                _realRoads, _realTowns, _realVersion);
            return _map;
        }
    }
}
