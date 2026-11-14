using TruckingAdvisor.Core.Events;
using TruckingAdvisor.Shared;
using TruckingAdvisor.Shared.Diagnostics;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Presentation;

/// <summary>Things the game itself reports: fines, tolls, fares, refuels; plus units and the game's name.</summary>
public sealed partial class MainDashboardViewModel
{
    private readonly GameEventDetector _gameEvents = new();
    private uint _unitsGameId = uint.MaxValue;
    private UnitPreference _unitsPreference;

    /// <summary>Set by the window when started with --replay (a recorded session is playing).</summary>
    public bool IsReplay { get; set; }

    public string GameName => _current.GameId == 1 ? "ETS2" : "ATS";

    private void InitGameEvents() => _gameEvents.EventOccurred += (_, e) => OnGameEvent(e);

    /// <summary>Follows the Units setting; "Auto" switches with the game (ATS imperial, ETS2 metric).</summary>
    private void ApplyUnits(uint gameId)
    {
        if (gameId == _unitsGameId && _aiSettings.Units == _unitsPreference) return;
        _unitsGameId = gameId;
        _unitsPreference = _aiSettings.Units;
        bool metric = Units.Resolve(_aiSettings.Units, gameId);
        if (metric == Units.Metric) return;
        Units.Metric = metric;
        AppLog.Info($"Units: {(metric ? "metric" : "imperial")}");
        _postToUi(() => { RefreshTrends(); RefreshContacts(); RaiseAll(); });
    }

    /// <summary>Call after the settings dialog closes so a unit change shows immediately.</summary>
    public void RefreshUnits()
    {
        ApplyUnits(_current.GameId);
        _postToUi(RaiseAll);
    }

    private void OnGameEvent(GameEvent e)
    {
        _trips.RecordGameEvent(e);
        uint game = _telemetry.LatestSnapshot.GameId;
        string money = Units.Money(e.Amount, game);
        (string label, string text) = e.Kind switch
        {
            GameEventKind.Fine => ("Fine", $"{money} for {GameEvent.OffenceText(e.Detail)}."),
            GameEventKind.Toll => ("Toll", $"{money} paid."),
            GameEventKind.Ferry => ("Ferry", $"{money}{(e.Detail.Length > 0 ? $", {e.Detail}" : "")}."),
            GameEventKind.Train => ("Train", $"{money}{(e.Detail.Length > 0 ? $", {e.Detail}" : "")}."),
            _ => ("Fuel", $"Took on {Units.Vol(e.Gallons):F0} {Units.VolWord}.")
        };
        AppLog.Info($"Game event: {label} {text}");

        // Only fines are worth a voice line; tolls, fares and refuels just go in the list.
        if (e.Kind == GameEventKind.Fine)
        {
            if (_aiSettings.AnnounceFines && _aiSettings.CoDriver != CoDriverPersonality.Minimal)
                _audio.EnqueueAnnouncement($"That's a {SpokenMoney(e.Amount, game)} fine for {GameEvent.OffenceText(e.Detail)}.", AudioPriority.Normal, "advisor");
            if (_trips.ActiveTrip is not null) _dispatcher?.OnFine(e.Amount, GameEvent.OffenceText(e.Detail), game);
        }

        var row = new DrivingIncident { Timestamp = e.Time, IncidentType = "Game" + e.Kind, Severity = label, AdvisoryMessage = text };
        _postToUi(() =>
        {
            RecentIncidents.Insert(0, row);
            while (RecentIncidents.Count > 50) RecentIncidents.RemoveAt(RecentIncidents.Count - 1);
        });
    }

    private static string SpokenMoney(long amount, uint gameId) => $"{amount:N0} {(gameId == 1 ? "euro" : "dollar")}";
}
