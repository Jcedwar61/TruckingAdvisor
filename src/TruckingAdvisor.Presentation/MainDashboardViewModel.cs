using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TruckingAdvisor.AI;
using TruckingAdvisor.Core.Advisors;
using TruckingAdvisor.Core.Career;
using TruckingAdvisor.Core.Trips;
using TruckingAdvisor.Data;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Presentation;

/// <summary>
/// Main dashboard state. Telemetry arrives on a background thread; coaching and trip tracking run
/// there (cheap, deterministic), and only display updates are posted to the UI thread via
/// <c>postToUi</c>. AI work never runs on the telemetry path.
/// </summary>
public sealed partial class MainDashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan UiRefresh = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AdvisorRefresh = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ChatterCheck = TimeSpan.FromSeconds(30);

    private readonly ITelemetryProvider _telemetry;
    private readonly IDrivingAnalyzer _analyzer;
    private readonly FuelAndMaintenanceAdvisor _advisor;
    private readonly TripTracker _trips;
    private readonly IAudioPriorityManager _audio;
    private readonly CBRadioService _radio;
    private readonly TruckingAdvisorDatabase? _db;
    private readonly AiSettings _aiSettings;
    private readonly Action<Action> _postToUi;
    private readonly Random _random = new();
    private readonly CancellationTokenSource _cts = new();

    private AtsTelemetrySnapshot _current = new();
    private AtsTelemetrySnapshot? _previous;
    private DateTimeOffset _lastUiPush, _lastAdvice, _lastChatterCheck;
    private string[] _lastAdvisories = Array.Empty<string>();
    private string _radioNotice = string.Empty;
    private string _lastReport = string.Empty;
    private double? _lastTripMpg; // kept off the UI collection so the telemetry thread can read it safely

    public VirtualCompanyProfile Company { get; }
    public CareerProfile Career { get; }
    public ObservableCollection<DrivingIncident> RecentIncidents { get; } = new();
    public ObservableCollection<string> ActiveAdvisories { get; } = new();
    public ObservableCollection<string> RadioTranscriptLog { get; } = new();
    public ObservableCollection<DeliveryTripRecord> TripHistory { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainDashboardViewModel(
        ITelemetryProvider telemetry,
        IDrivingAnalyzer analyzer,
        FuelAndMaintenanceAdvisor advisor,
        TripTracker trips,
        IAudioPriorityManager audio,
        CBRadioService radio,
        VirtualCompanyProfile company,
        CareerProfile career,
        AiSettings aiSettings,
        Action<Action> postToUi,
        TruckingAdvisorDatabase? db = null)
    {
        _telemetry = telemetry;
        _analyzer = analyzer;
        _advisor = advisor;
        _trips = trips;
        _audio = audio;
        _radio = radio;
        _db = db;
        _aiSettings = aiSettings;
        _postToUi = postToUi;
        Company = company;
        Career = career;

        if (_db is not null)
            foreach (var t in _db.LoadTrips(50)) TripHistory.Add(t);
        _lastTripMpg = TripHistory.FirstOrDefault(t => t.State == DeliveryState.Completed && t.AverageMpg > 0)?.AverageMpg;

        _telemetry.TelemetryUpdated += OnTelemetryUpdated;
        _analyzer.IncidentDetected += OnIncidentDetected;
        _trips.TripFinished += OnTripFinished;
        _radio.TransmissionReceived += OnTransmissionReceived;
        _radio.TruckerUpdated += (_, t) => SafeDb(db => db.SaveTrucker(t));
        _radio.Notice += (_, msg) => _postToUi(() => RadioNotice = msg);
        _radio.StateChanged += (_, _) => _postToUi(() => Raise(nameof(RadioStateText)));
        InitFeatures();
    }

    // ---- Bindable display properties ----

    public AtsTelemetrySnapshot CurrentTelemetry
    {
        get => _current;
        private set { _current = value; RaiseAll(); }
    }

    public string ConnectionStatusText => _current.ConnectionState switch
    {
        ConnectionState.Connected => "● CONNECTED TO ATS",
        ConnectionState.GamePaused => "● ATS PAUSED",
        ConnectionState.SimulationFallback => "● DEMO DATA (not from the game)",
        ConnectionState.Connecting => "● CONNECTING...",
        _ => "● NOT CONNECTED - start ATS with the telemetry plugin installed"
    };
    public bool IsDemo => _current.IsDemoData;
    public string SpeedText => _current.IsLive || _current.IsDemoData ? $"{_current.SpeedMph:F0}" : "--";
    public string SpeedLimitText => _current.SpeedLimitMph > 0 ? $"LIMIT {_current.SpeedLimitMph:F0} MPH" : "LIMIT --";
    public bool IsSpeeding => _current.SpeedLimitMph > 0 && _current.SpeedMph >= _current.SpeedLimitMph + 1;
    public string RpmText => _current.EngineRpm > 0 ? $"{_current.EngineRpm:F0} RPM" : "-- RPM";
    public string GearText => _current.DisplayedGear switch { 0 => "GEAR N", < 0 => $"GEAR R{-_current.DisplayedGear}", var g => $"GEAR {g}" };
    public string FuelText => _current.HasFuelCapacity ? $"{_current.FuelPercent:F0}%" : "--";
    public string RangeText => _current.EstimatedRangeMiles > 0 ? $"RANGE {_current.EstimatedRangeMiles:F0} MI" : "RANGE --";
    public string MpgText => _current.HasFuelEconomy ? $"AVG {_current.AverageMpg:F1} MPG" : "AVG -- MPG";
    public string AirText => _current.AirPressurePsi > 0 ? $"AIR {_current.AirPressurePsi:F0} PSI" : "AIR --";
    public string EngineTempText => _current.WaterTempFahrenheit > 0 && _current.IsLive ? $"WATER {_current.WaterTempFahrenheit:F0}°F" : "WATER --";
    public string CruiseText => _current.CruiseControlOn ? $"CRUISE {_current.CruiseControlSpeedMph:F0}" : "CRUISE OFF";
    public string GameTimeText => _current.GameTimeMinutes > 0 ? _current.FormattedGameTime : "--:--";
    public string RestText =>
        (_current.HasRestData ? $"Rest due in {_current.NextRestStopGameMinutes / 60}h {_current.NextRestStopGameMinutes % 60:D2}m (game time)" : "Rest data unavailable")
        + $"  •  Session {(int)SessionDuration.TotalHours}h {SessionDuration.Minutes:D2}m (real time)";

    public string CargoText => _current.JobActive && !string.IsNullOrEmpty(_current.CargoName)
        ? $"{_current.CargoName}{(_current.CargoWeightLbs > 0 ? $" ({_current.CargoWeightLbs:N0} lbs)" : "")}"
        : "No active delivery";
    public string RouteText => _current.JobActive
        ? $"{Fallback(_current.OriginCity, "?")}  ->  {Fallback(_current.DestinationCity, "?")}"
        : string.Empty;
    public string RemainingText => _current.JobActive && _current.NavigationDistanceRemainingMiles > 0
        ? $"{_current.NavigationDistanceRemainingMiles:F0} mi remaining, ETA {(int)(_current.NavigationTimeRemainingMinutes / 60)}h {(int)_current.NavigationTimeRemainingMinutes % 60:D2}m (navigation)"
        : string.Empty;
    public double TripProgressPercent => _current.JobActive && _current.PlannedDistanceMiles > 0
        ? Math.Clamp(100.0 * (1 - _current.NavigationDistanceRemainingMiles / _current.PlannedDistanceMiles), 0, 100) : 0;
    public string TripStateText => _trips.ActiveTrip is { } t
        ? $"{t.State}{(t.StartedMidDelivery ? " (started mid-delivery)" : "")}" : "No trip";

    public string ScoreText => _trips.ActiveTrip is { } t ? $"{t.ScoreBreakdown.FinalScore}/100" : "--";
    public string RatingText => _trips.ActiveTrip?.ScoreBreakdown.RatingLabel ?? "Score appears during a delivery";

    public string RadioChannelText => $"CHANNEL {_radio.ActiveChannel} - {ChannelName(_radio.ActiveChannel)}";
    public string RadioStateText => _radio.CurrentState switch
    {
        CBRadioState.PlayerTransmitting => "TX - TRANSMITTING",
        CBRadioState.ProcessingSpeech => "PROCESSING...",
        CBRadioState.GeneratingResponse => "WAITING FOR REPLY...",
        CBRadioState.PlayingAiResponse or CBRadioState.PlayingTransmission or CBRadioState.IncomingTransmission => "RX - RECEIVING",
        CBRadioState.RadioOff => "RADIO OFF",
        _ => "MIC READY - HOLD PTT TO TRANSMIT"
    };
    public string PartnerText => _radio.ActivePartner is { } p
        ? $"LAST HEARD: {p.Handle.ToUpperInvariant()} ({p.Truck} • {p.Relationship})"
        : "Simulated CB radio - voices are app characters, not real nearby trucks";
    public string AiStatusText => _radio.AiAvailable ? $"AI: ON ({_aiSettings.ChatModel})" : "AI: OFF (offline radio lines)";
    public string RadioNotice { get => _radioNotice; private set { _radioNotice = value; Raise(); } }
    /// <summary>Shows a short message under the radio (call on the UI thread).</summary>
    public void ShowNotice(string message) => RadioNotice = message;
    public string LastReport { get => _lastReport; private set { _lastReport = value; Raise(); } }

    public static string ChannelName(int ch) => ch switch
    {
        19 => "HIGHWAY TRAFFIC",
        9 => "EMERGENCY",
        17 => "REGIONAL TRAFFIC",
        21 => "CASUAL CONVERSATION",
        25 => "TRUCK STOP TALK",
        30 => "COMPANY DISPATCH",
        _ => "OPEN CHANNEL"
    };

    public static readonly int[] Channels = { 19, 9, 17, 21, 25, 30 };

    public void CycleChannel()
    {
        int i = Array.IndexOf(Channels, _radio.ActiveChannel);
        _radio.SwitchChannel(Channels[(i + 1) % Channels.Length]);
        Raise(nameof(RadioChannelText));
    }

    // ---- Telemetry path (background thread) ----

    private void OnTelemetryUpdated(object? sender, AtsTelemetrySnapshot snapshot)
    {
        _analyzer.EvaluateSnapshot(snapshot, _previous);
        _trips.Update(snapshot);
        OnTelemetryFeatures(snapshot, _previous);
        _previous = snapshot;

        var now = snapshot.Timestamp;
        if (now - _lastUiPush >= UiRefresh)
        {
            _lastUiPush = now;
            _postToUi(() => CurrentTelemetry = snapshot);
        }

        if (now - _lastAdvice >= AdvisorRefresh)
        {
            _lastAdvice = now;
            _dispatcher?.Evaluate(snapshot, _trips.ActiveTrip);
            OnSlowTick(snapshot);
            var advice = _advisor.GenerateAdvisories(snapshot, LastTripMpg(), LastServiceOdometer()).ToArray();
            if (!advice.SequenceEqual(_lastAdvisories))
            {
                _lastAdvisories = advice;
                _postToUi(() =>
                {
                    ActiveAdvisories.Clear();
                    foreach (var a in advice) ActiveAdvisories.Add(a);
                });
            }
        }

        if (now - _lastChatterCheck >= ChatterCheck)
        {
            _lastChatterCheck = now;
            MaybeStartChatter(snapshot);
        }
    }

    private void MaybeStartChatter(AtsTelemetrySnapshot s)
    {
        if (s.IsPaused || !(s.IsLive || s.IsDemoData)) return;
        double chance = _aiSettings.RadioActivity switch
        {
            RadioActivitySetting.Quiet => 0.04,
            RadioActivitySetting.Normal => 0.10,
            RadioActivitySetting.Busy => 0.20,
            RadioActivitySetting.VeryBusy => 0.35,
            _ => 0
        };
        double roll = _random.NextDouble();
        if (roll >= chance) return;
        // Fire and forget off the telemetry thread; the radio ignores this if it's busy.
        bool banter = roll < chance / 3;
        _ = Task.Run(() => banter
            ? _radio.TriggerAiToAiBanterAsync(s, _cts.Token)
            : _radio.TriggerDynamicEncounterAsync(s, _cts.Token));
    }

    private void OnIncidentDetected(object? sender, DrivingIncident incident)
    {
        _trips.RecordIncident(incident);
        var priority = incident.Severity switch
        {
            "Critical" => AudioPriority.Critical,
            "High" => AudioPriority.High,
            _ => AudioPriority.Normal
        };
        if (incident.IncidentType == "HarshBraking") AchievementManager.OnHarshBraking(Career);
        // The onboard co-driver words it in the chosen personality (or stays quiet in Minimal mode).
        if (CoDriverPhrase(incident) is { } spoken)
            _audio.EnqueueAnnouncement(spoken, priority, "advisor");
        _postToUi(() =>
        {
            RecentIncidents.Insert(0, incident);
            while (RecentIncidents.Count > 50) RecentIncidents.RemoveAt(RecentIncidents.Count - 1);
        });
    }

    private void OnTripFinished(object? sender, DeliveryTripRecord trip)
    {
        int levels = CareerManager.ApplyTrip(Company, Career, trip);
        if (trip.State == DeliveryState.Completed && trip.AverageMpg > 0) _lastTripMpg = trip.AverageMpg;
        var unlocked = OnTripFinishedFeatures(trip);
        SafeDb(db =>
        {
            db.SaveTrip(trip);
            db.SaveCompany(Company);
            db.SaveCareer(Career);
        });

        string report = TruckingAdvisorDatabase.ExportDeliveryReportText(trip);
        if (trip.State == DeliveryState.Completed)
        {
            _audio.EnqueueAnnouncement(
                $"Delivery complete. Score {trip.ScoreBreakdown.FinalScore}, {trip.ScoreBreakdown.RatingLabel}.{(levels > 0 ? $" You reached career level {Career.Level}." : "")}",
                AudioPriority.Normal);
        }
        foreach (var a in unlocked)
            _audio.EnqueueAnnouncement($"Achievement unlocked: {a.Title}.", AudioPriority.Low, "notifications");
        _postToUi(() =>
        {
            LastReport = report;
            TripHistory.Insert(0, trip);
            RaiseAll();
        });
    }

    private void OnTransmissionReceived(object? sender, (string Speaker, string Handle, string Transcript, int Channel) e)
    {
        string line = $"[CH {e.Channel}] {e.Handle}: \"{e.Transcript}\"";
        _postToUi(() =>
        {
            RadioTranscriptLog.Insert(0, line);
            while (RadioTranscriptLog.Count > 200) RadioTranscriptLog.RemoveAt(RadioTranscriptLog.Count - 1);
            Raise(nameof(PartnerText));
        });
    }

    // ---- helpers ----

    private double? LastTripMpg() => _lastTripMpg;

    private float? LastServiceOdometer()
    {
        var records = _db?.LoadServiceRecordsCached();
        return records is { Count: > 0 } ? records[0].OdometerAtServiceMiles : null;
    }

    private void SafeDb(Action<TruckingAdvisorDatabase> action)
    {
        if (_db is null) return;
        try { action(_db); }
        catch (Exception ex) { _postToUi(() => RadioNotice = $"Could not save data: {ex.Message}"); }
    }

    private static string Fallback(string s, string f) => string.IsNullOrWhiteSpace(s) ? f : s;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Most display properties derive from the snapshot; refresh them together.</summary>
    private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public void Dispose()
    {
        SaveMapMemories();
        _cts.Cancel();
        _telemetry.TelemetryUpdated -= OnTelemetryUpdated;
        _analyzer.IncidentDetected -= OnIncidentDetected;
        _trips.TripFinished -= OnTripFinished;
        _radio.TransmissionReceived -= OnTransmissionReceived;
        _cts.Dispose();
    }
}
