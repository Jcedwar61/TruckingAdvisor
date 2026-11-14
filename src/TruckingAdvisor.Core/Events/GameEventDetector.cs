using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Core.Events;

public enum GameEventKind { Fine, Toll, Ferry, Train, Refuel }

/// <summary>Something the game reported: a fine, a toll, a ferry or train fare, or a refuel.</summary>
/// <param name="Amount">In-game currency (0 for refuels: the game doesn't report fuel cost).</param>
/// <param name="Detail">Offence id, ferry/train route, or empty.</param>
/// <param name="Gallons">Fuel added, for refuels.</param>
public sealed record GameEvent(GameEventKind Kind, long Amount, string Detail, float Gallons, DateTimeOffset Time)
{
    /// <summary>Readable offence, e.g. "red_signal" → "running a red light".</summary>
    public static string OffenceText(string id) => id.Trim().ToLowerInvariant() switch
    {
        "speeding" or "speeding_camera" => "speeding",
        "red_signal" => "running a red light",
        "crash" => "a collision",
        "no_lights" => "driving without lights",
        "wrong_way" => "driving the wrong way",
        "avoid_sleeping" => "driving without rest",
        "avoid_weighing" => "skipping a weigh station",
        "avoid_inspection" => "skipping an inspection",
        "illegal_trailer" => "an illegal trailer",
        "illegal_border_crossing" => "an illegal border crossing",
        "hard_shoulder_violation" => "driving on the shoulder",
        "damaged_vehicle_usage" => "driving a damaged truck",
        "generic" or "" => "a traffic offence",
        var other => other.Replace('_', ' ')
    };
}

/// <summary>
/// Watches the plugin's event flags (which flip on every event) and fuel level, and raises one
/// <see cref="GameEvent"/> per real event. Demo data never raises events.
///
/// The plugin flips a flag first and fills in the amount a moment later, so an event is reported
/// on the snapshot AFTER the flip, using that snapshot's amount.
/// </summary>
public sealed class GameEventDetector
{
    private AtsTelemetrySnapshot? _last;
    private bool _pendingFine, _pendingToll, _pendingFerry, _pendingTrain;
    private float _refuelGallons;
    private DateTimeOffset _lastFuelRise;
    /// <summary>A refuel is reported once the tank stops rising for this long.</summary>
    public static readonly TimeSpan RefuelSettle = TimeSpan.FromSeconds(3);

    public event EventHandler<GameEvent>? EventOccurred;

    public void Update(AtsTelemetrySnapshot s)
    {
        if (s.IsDemoData || !s.IsLive)
        {
            // The game or plugin may restart and reset the flags: start over when it's back.
            _last = null;
            _pendingFine = _pendingToll = _pendingFerry = _pendingTrain = false;
            _refuelGallons = 0;
            return;
        }
        if (_last is null) { _last = s; return; }

        if (_pendingFine) { _pendingFine = false; Raise(GameEventKind.Fine, s.FineAmount, s.FineOffence, 0, s); }
        if (_pendingToll) { _pendingToll = false; Raise(GameEventKind.Toll, s.TollAmount, "", 0, s); }
        if (_pendingFerry) { _pendingFerry = false; Raise(GameEventKind.Ferry, s.FerryAmount, s.FerryRoute, 0, s); }
        if (_pendingTrain) { _pendingTrain = false; Raise(GameEventKind.Train, s.TrainAmount, s.TrainRoute, 0, s); }

        if (s.FinedFlag != _last.FinedFlag) _pendingFine = true;
        if (s.TollgateFlag != _last.TollgateFlag) _pendingToll = true;
        if (s.FerryFlag != _last.FerryFlag) _pendingFerry = true;
        if (s.TrainFlag != _last.TrainFlag) _pendingTrain = true;

        // Refuel: the tank level rises while (nearly) stopped. Same truck only, so swapping trucks isn't a refuel.
        float rise = s.FuelGallons - _last.FuelGallons;
        bool sameTruck = string.Equals(s.TruckId, _last.TruckId, StringComparison.Ordinal);
        if (sameTruck && rise > 0.02f && s.SpeedMph < 3f && _last.FuelGallons > 0)
        {
            _refuelGallons += rise;
            _lastFuelRise = s.Timestamp;
        }
        else if (_refuelGallons > 0 && (s.Timestamp - _lastFuelRise >= RefuelSettle || !sameTruck))
        {
            float gallons = _refuelGallons;
            _refuelGallons = 0;
            if (gallons >= 1f && sameTruck) Raise(GameEventKind.Refuel, 0, "", gallons, s);
        }

        _last = s;
    }

    private void Raise(GameEventKind kind, long amount, string detail, float gallons, AtsTelemetrySnapshot s) =>
        EventOccurred?.Invoke(this, new GameEvent(kind, Math.Max(0, amount), detail, gallons, s.Timestamp));
}
