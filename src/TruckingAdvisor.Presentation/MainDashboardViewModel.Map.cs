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
    string Summary);

/// <summary>The self-building map: roads you've driven, cities you've delivered to, and where you are now.</summary>
public sealed partial class MainDashboardViewModel
{
    private MapSnapshot? _map;
    private int _mapRoadVersion = -1;
    private IReadOnlyList<float[]> _mapRoads = Array.Empty<float[]>();

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
            var cities = world.Cities.Cities.Values.Select(c => new MapCity(c.Name, c.X, c.Z)).ToList();
            MapCity? dest = null;
            if (!string.IsNullOrEmpty(s.DestinationCity) && world.Cities.Cities.TryGetValue(s.DestinationCity, out var d))
                dest = new MapCity(d.Name, d.X, d.Z);
            bool truck = (s.IsLive || s.IsDemoData) && !(s.WorldX == 0 && s.WorldZ == 0);
            string game = world.GameId == 1 ? "Euro Truck Simulator 2" : "American Truck Simulator";
            string summary = roads.IsEmpty
                ? $"{game}: no roads recorded yet. Drive and the map draws itself."
                : $"{game}: {Units.DistText(roads.KnownRoadMiles)} of road driven, {cities.Count} cities learned";
            _map = new MapSnapshot(world.Key, _mapRoads, _mapRoadVersion, cities, truck, s.WorldX, s.WorldZ, s.HeadingDegrees, dest, summary);
            return _map;
        }
    }
}
