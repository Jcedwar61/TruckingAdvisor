using TruckingAdvisor.Shared;
using TruckingAdvisor.Shared.Enums;

namespace TruckingAdvisor.Presentation;

/// <summary>
/// The data behind TruckingAdvisor's SimHub panel (served at /simhub/state.json and shown in the SimHub
/// dashboard's web view). Built from the same display text as the main window, so both always agree.
/// </summary>
public sealed partial class MainDashboardViewModel
{
    /// <summary>
    /// A ready-made snapshot for the SimHub panel. Call it on the UI thread (it reads the radio messages and
    /// coaching lists, which only change there); the result is immutable, so the web server can send it from any thread.
    /// </summary>
    public object BuildSimHubPanelState()
    {
        string speedText = SpeedText;
        string NoDash(string s) => s.Replace("LIMIT ", "").Replace("RANGE ", "").Replace("AVG ", "");
        var trip = _trips.ActiveTrip;
        return new
        {
            available = true,
            demo = IsDemo,
            connection = new
            {
                text = ConnectionStatusText,
                live = _current.IsLive,
                demo = _current.IsDemoData,
                game = HasData ? GameName : null
            },
            gameTime = GameTimeText,
            rest = _current.HasRestData ? $"Rest due in {_current.NextRestStopGameMinutes / 60}h {_current.NextRestStopGameMinutes % 60:D2}m" : "",
            location = HasData && !LocationText.StartsWith("Location:", StringComparison.Ordinal) ? LocationText : "",
            gradeAhead = GradeAheadText,
            truck = new
            {
                speed = speedText,
                limit = SpeedLimitNumber,
                unit = Units.SpeedUnit,
                speeding = IsSpeeding,
                gear = GearText.Replace("GEAR ", ""),
                fuel = FuelText,
                range = RangeText.EndsWith("--", StringComparison.Ordinal) ? "" : RangeText,
                economy = _current.HasFuelEconomy ? NoDash(MpgText) : "--",
                cruise = CruiseText
            },
            trip = new
            {
                active = _current.JobActive,
                cargo = CargoText,
                route = RouteText,
                destination = _current.JobActive ? _current.DestinationCity : "",
                distanceLeft = _current.JobActive && _current.NavigationDistanceRemainingMiles > 0
                    ? Units.DistText(_current.NavigationDistanceRemainingMiles) : "",
                eta = _current.JobActive && _current.NavigationDistanceRemainingMiles > 0
                    ? $"{(int)(_current.NavigationTimeRemainingMinutes / 60)}h {(int)_current.NavigationTimeRemainingMinutes % 60:D2}m" : "",
                remaining = RemainingText,
                progress = TripProgressPercent,
                state = Spaced(TripStateText),
                score = ScoreDialText,
                scoreValue = ScoreValue,
                rating = trip is null ? "Score appears during a delivery" : RatingText
            },
            radio = new
            {
                channel = _radio.ActiveChannel,
                channelName = ChannelNameText,
                state = RadioStateText,
                mode = _radio.CurrentState switch
                {
                    CBRadioState.PlayerTransmitting => "tx",
                    CBRadioState.PlayingAiResponse or CBRadioState.PlayingTransmission or CBRadioState.IncomingTransmission => "rx",
                    CBRadioState.ProcessingSpeech or CBRadioState.GeneratingResponse => "busy",
                    CBRadioState.RadioOff => "off",
                    _ => "idle"
                },
                partner = _radio.ActivePartner is null ? "" : PartnerText,
                quiet = IsQuiet,
                ai = AiStatusText,
                messages = RadioMessages.Skip(Math.Max(0, RadioMessages.Count - 6)).Select(m => new
                {
                    handle = m.Handle,
                    text = m.Text,
                    channel = m.Channel,
                    mine = m.IsMine,
                    dispatch = m.IsDispatch,
                    time = m.Time.ToString("HH:mm")
                }).ToArray()
            },
            coaching = RecentIncidents.Take(3).Select(i => new
            {
                type = i.IncidentType,
                severity = i.Severity,
                message = i.AdvisoryMessage,
                time = i.Timestamp.ToLocalTime().ToString("HH:mm"),
                ageSeconds = Math.Max(0, (DateTimeOffset.UtcNow - i.Timestamp).TotalSeconds)
            }).ToArray(),
            advisories = ActiveAdvisories.Take(3).ToArray(),
            career = new
            {
                level = LevelTitle,
                xp = XpText,
                xpPercent = XpPercent,
                deliveries = StatDeliveries,
                avgScore = StatAvgScore,
                damageFree = StatDamageFree,
                streak = Career.CurrentDamageFreeStreak
            }
        };
    }
}
