using TruckingAdvisor.Shared;
using System.Collections.ObjectModel;
using TruckingAdvisor.AI;
using TruckingAdvisor.Core.Career;
using TruckingAdvisor.Core.Coaching;
using TruckingAdvisor.Core.Insights;
using TruckingAdvisor.Core.Mapping;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Presentation;

/// <summary>One CB contact for the Contacts page.</summary>
public sealed record ContactView(string Id, string Handle, string Name, string Truck, string HomeState, string Relationship,
    string LastContact, int Conversations, IReadOnlyList<string> Memories,
    string Personality = "", string Topics = "", string Region = "", int Experience = 0, double RelationshipPercent = 0)
{
    public string NoMemoriesHint => Memories.Count == 0 ? $"You haven't talked with {Handle} yet. Give them a call." : "";
}

/// <summary>Achievements, insights, dispatcher, co-driver, voice commands and contacts.</summary>
public sealed partial class MainDashboardViewModel
{
    private readonly DateTimeOffset _sessionStart = DateTimeOffset.UtcNow;
    private readonly object _tripsLock = new();
    private readonly List<DeliveryTripRecord> _allTrips = new(); // thread-safe copy for background analysis
    private DispatcherService? _dispatcher;
    private DateTimeOffset? _parkedSince;
    private string _lastTruck = string.Empty;
    private string? _lastSpokenAdvice;

    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckingAdvisor");
    private static string LocationsPath => Path.Combine(DataDir, "learned-cities.json");
    private static string GradesPath => Path.Combine(DataDir, "learned-grades.json");
    private readonly LocationMemory _locations = LocationMemory.Load(LocationsPath);
    private readonly GradeMemory _grades = GradeMemory.Load(GradesPath);
    private DateTimeOffset _lastGradeWarning = DateTimeOffset.MinValue;
    private string _locationText = "Location: learning from your deliveries";
    private string _gradeAheadText = string.Empty;

    /// <summary>Approximate location from cities learned on your own deliveries.</summary>
    public string LocationText { get => _locationText; private set { if (_locationText != value) { _locationText = value; Raise(); } } }
    /// <summary>Steep downgrade ahead that you've driven before (empty when none).</summary>
    public string GradeAheadText { get => _gradeAheadText; private set { if (_gradeAheadText != value) { _gradeAheadText = value; Raise(); } } }
    public int LearnedCityCount => _locations.Cities.Count;

    /// <summary>The live snapshot plus the learned approximate city/state (for the AI's context).</summary>
    public AtsTelemetrySnapshot EnrichedSnapshot
    {
        get
        {
            var s = _telemetry.LatestSnapshot;
            var city = NearestCityName(s);
            return city is null ? s : s with { CurrentCity = city, CurrentState = CityStates.StateFor(city) ?? string.Empty };
        }
    }

    private string? NearestCityName(AtsTelemetrySnapshot s)
    {
        var d = _locations.Describe(s.WorldX, s.WorldZ);
        if (d is null) return null;
        string rest = d.StartsWith("in ") ? d[3..] : d.StartsWith("near ") ? d[5..] : d;
        int comma = rest.LastIndexOf(", ", StringComparison.Ordinal);
        return comma > 0 ? rest[..comma] : rest;
    }

    /// <summary>Called once a second from the telemetry thread.</summary>
    private void OnSlowTick(AtsTelemetrySnapshot s)
    {
        if (!s.IsLive || s.IsPaused) return;
        _grades.Record(s.WorldX, s.WorldZ, s.CalculatedGradePercent, s.HeadingDegrees, s.SpeedMph);

        var where = _locations.Describe(s.WorldX, s.WorldZ);
        string location = where is null
            ? $"Location: unknown here ({_locations.Cities.Count} cities learned from your deliveries)"
            : $"Location: {where} (approximate, learned)";

        string ahead = string.Empty;
        if (s.SpeedMph > 20 && _grades.Ahead(s.WorldX, s.WorldZ, s.HeadingDegrees) is { } g)
        {
            ahead = $"Steep downgrade ahead: about {Math.Abs(g.Grade):F0}% (you've driven it before)";
            if (s.Timestamp - _lastGradeWarning > TimeSpan.FromMinutes(3))
            {
                _lastGradeWarning = s.Timestamp;
                _audio.EnqueueAnnouncement($"Steep downgrade coming up, about {Math.Abs(g.Grade):F0} percent. Check your speed and get on the engine brake early.",
                    AudioPriority.High, "advisor");
            }
        }
        _postToUi(() => { LocationText = location; GradeAheadText = ahead; });
    }

    private void LearnTripCity(DeliveryTripRecord trip, AtsTelemetrySnapshot? s, bool atStart)
    {
        if (s is null || !s.IsLive || s.SpeedMph > 15) return; // only when stopped at the company
        if (atStart && !trip.StartedMidDelivery) _locations.Learn(trip.Origin, s.WorldX, s.WorldZ);
        if (!atStart && trip.State == DeliveryState.Completed) _locations.Learn(trip.Destination, s.WorldX, s.WorldZ);
    }

    private void SaveMapMemories()
    {
        _locations.Save(LocationsPath);
        _grades.Save(GradesPath);
    }

    public ObservableCollection<AchievementItem> Achievements { get; } = new();
    public ObservableCollection<string> Insights { get; } = new();
    public ObservableCollection<ContactView> Contacts { get; } = new();
    public ObservableCollection<string> TruckEconomyRows { get; } = new();

    // Trend charts (oldest -> newest completed trips, last 30).
    public IReadOnlyList<double> ScoreTrend { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> MpgTrend { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> IncidentTrend { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> MilesTrend { get; private set; } = Array.Empty<double>();

    /// <summary>All recorded trips (thread-safe copy) for exports.</summary>
    public IReadOnlyList<DeliveryTripRecord> AllTrips { get { lock (_tripsLock) return _allTrips.ToList(); } }

    private void RefreshTrends()
    {
        var done = AllTrips.Where(t => t.State == DeliveryState.Completed).OrderBy(t => t.StartedAt).TakeLast(30).ToList();
        ScoreTrend = done.Select(t => (double)t.ScoreBreakdown.FinalScore).ToList();
        MpgTrend = done.Where(t => t.AverageMpg > 0).Select(t => Units.Economy(t.AverageMpg)).ToList();
        IncidentTrend = done.Where(t => t.DistanceMiles >= 10)
            .Select(t => (t.SpeedingIncidents + t.HarshBrakingIncidents + t.AggressiveAccelerationIncidents) * 100.0 / Units.Dist(t.DistanceMiles)).ToList();
        MilesTrend = done.Select(t => Units.Dist(t.DistanceMiles)).ToList();
        Raise(nameof(ScoreTrend)); Raise(nameof(MpgTrend)); Raise(nameof(IncidentTrend)); Raise(nameof(MilesTrend));
    }

    public TimeSpan SessionDuration => DateTimeOffset.UtcNow - _sessionStart;

    // ---- Numbers for the dials ----
    private bool HasData => _current.IsLive || _current.IsDemoData;
    public double SpeedValue => HasData ? Units.Speed(_current.SpeedMph) : 0;
    public double SpeedLimitValue => HasData ? Units.Speed(_current.SpeedLimitMph) : 0;
    /// <summary>Dial top end: rounded up from the engine's max RPM (2500 until known).</summary>
    public double RpmMax => _current.MaxEngineRpm > 0 ? Math.Ceiling(_current.MaxEngineRpm / 500.0) * 500 : 2500;
    public double RpmValue => HasData ? _current.EngineRpm : 0;
    public double RpmRedFrom => _current.MaxEngineRpm > 0 ? _current.MaxEngineRpm * 0.92 : 0;
    public string RpmDialText => _current.EngineRpm > 0 ? $"{_current.EngineRpm:F0}" : "--";
    public double FuelPercentValue => _current.HasFuelCapacity ? _current.FuelPercent : 0;
    public bool IsFuelLow => _current.HasFuelCapacity && _current.FuelPercent <= 15;
    public double ScoreValue => _trips.ActiveTrip?.ScoreBreakdown.FinalScore ?? 0;
    public string ScoreDialText => _trips.ActiveTrip is { } t ? t.ScoreBreakdown.FinalScore.ToString() : "--";

    // ---- CB radio face ----
    public string ChannelNumberText => _radio.ActiveChannel.ToString("00");
    public string ChannelNameText => ChannelName(_radio.ActiveChannel);
    public bool IsTransmitting => _radio.CurrentState == CBRadioState.PlayerTransmitting;
    public bool IsReceiving => _radio.CurrentState is CBRadioState.PlayingAiResponse or CBRadioState.PlayingTransmission or CBRadioState.IncomingTransmission;
    public bool IsRadioBusy => _radio.CurrentState is CBRadioState.ProcessingSpeech or CBRadioState.GeneratingResponse;

    public void SelectChannel(int channel)
    {
        _radio.SwitchChannel(channel);
        RaiseAll();
    }

    private string _gameStatus = "Looking for American Truck Simulator...";
    /// <summary>Where the game was found and whether its telemetry plugin is installed.</summary>
    public string GameStatusText { get => _gameStatus; set { _gameStatus = value; Raise(); } }

    // ---- Career tiles ----
    public string StatDeliveries => Company.TotalDeliveries.ToString("N0");
    public string StatMiles => Units.Dist(Company.TotalMilesDriven).ToString("N0");
    public string StatMilesLabel => Units.DistWord.ToUpperInvariant();
    public string StatMpgLabel => $"LIFETIME {Units.EconomyUnit.ToUpperInvariant()}";
    public string SpeedUnitText => Units.SpeedUnit;
    public double SpeedGaugeMax => Units.Metric ? 160 : 100;
    public string MpgTrendTitle => $"Fuel economy ({Units.EconomyUnit})";
    public string IncidentTrendTitle => $"Coaching events per 100 {Units.DistWord}";
    public string MilesTrendTitle => $"Trip distance ({Units.DistWord})";
    public string StatDamageFree => Company.TotalDeliveries > 0 ? $"{100.0 * Company.DamageFreeDeliveries / Company.TotalDeliveries:F0}%" : "--";
    public string StatAvgScore => Company.TotalDeliveries > 0 ? Company.AverageDriverRating.ToString("F0") : "--";
    public string StatMpg => Company.LifetimeAverageMpg > 0 ? Units.Economy(Company.LifetimeAverageMpg).ToString("F1") : "--";
    public string StatIncome => Company.RecordedRevenue.ToString("N0");
    public string StatCities => Company.VisitedCities.Count.ToString();
    public string StatStreak => $"{Career.CurrentDamageFreeStreak} (best {Career.BestDamageFreeStreak})";
    public string CompanyTitle => string.IsNullOrWhiteSpace(Company.PlayerHandle)
        ? Company.CompanyName : $"{Company.CompanyName}  •  {Company.PlayerHandle}";
    public string LevelTitle => $"LEVEL {Career.Level}  •  {Career.RankTitle.ToUpperInvariant()}";
    public string XpText => $"{Career.CurrentXp:N0} / {Career.NextLevelXp:N0} XP to level {Career.Level + 1}";
    public double XpPercent => Career.NextLevelXp > 0 ? Math.Clamp(100.0 * Career.CurrentXp / Career.NextLevelXp, 0, 100) : 0;
    public string InsightsHint => Insights.Count == 0 ? "Insights appear after a few recorded deliveries (fuel trends, clean-driving streaks, favorite routes)." : "";
    public string TranscriptHint => RadioTranscriptLog.Count == 0
        ? "Quiet on the channel.\n\nHold push-to-talk (Left Alt by default, or the button below) and try:\n\"Breaker one-nine, anybody got a copy?\"\n\nNo mic? Type a message and press Enter." : "";

    /// <summary>Hails a trucker by name on channel 19, as if the player called them.</summary>
    public async Task CallContactAsync(string handle)
    {
        _radio.SwitchChannel(19);
        RaiseAll();
        _radio.BeginPushToTalk();
        await _radio.EndPushToTalkAsync($"Breaker one-nine, {handle}, you got a copy?");
    }

    public string CareerSummary =>
        $"Level {Career.Level} {Career.RankTitle} • {Career.CurrentXp:N0}/{Career.NextLevelXp:N0} XP • best score {Career.PersonalBestScore} • damage-free streak {Career.CurrentDamageFreeStreak} (best {Career.BestDamageFreeStreak})";

    public string CompanySummary =>
        $"{Company.CompanyName}{(string.IsNullOrWhiteSpace(Company.Headquarters) ? "" : $" • {Company.Headquarters}")} • driver {Company.DisplayHandle}\n" +
        $"{Company.TotalDeliveries} deliveries • {Units.DistText(Company.TotalMilesDriven)} • {(Company.TotalDeliveries > 0 ? 100.0 * Company.DamageFreeDeliveries / Company.TotalDeliveries : 0):F0}% damage-free • " +
        $"avg score {Company.AverageDriverRating:F0} • {Units.EconomyText(Company.LifetimeAverageMpg)} • income {Units.Money((long)Company.RecordedRevenue, _current.GameId)}{(Company.RecordedFines > 0 ? $", fines {Units.Money(Company.RecordedFines, _current.GameId)}" : "")} (in-game) • {Company.VisitedCities.Count} cities";

    /// <summary>Raised when a voice command asks to mute/unmute (the UI owns the speaker).</summary>
    public event EventHandler<bool>? MuteRequested;
    /// <summary>The player introduced a new handle; the UI should ask before saving it.</summary>
    public event EventHandler<string>? HandleChangeProposed;

    /// <summary>Connects the dispatcher; null disables proactive dispatch.</summary>
    public void AttachDispatcher(DispatcherService dispatcher) => _dispatcher = dispatcher;

    private void InitFeatures()
    {
        InitGameEvents();
        lock (_tripsLock) _allTrips.AddRange(TripHistory);
        _trips.StateChanged += (_, state) =>
        {
            if (state == DeliveryState.JobStarted && _trips.ActiveTrip is { } t)
            {
                LearnTripCity(t, _telemetry.LatestSnapshot, atStart: true);
                _dispatcher?.OnTripStarted(t);
                // One personal insight at the start of a load, low priority.
                var insight = ComputeInsights().FirstOrDefault();
                if (insight is not null && !_radio.IsQuietPeriod) _audio.EnqueueAnnouncement(insight, AudioPriority.Low, "advisor");
            }
        };
        _radio.CommandReceived += (_, c) => HandleVoiceCommand(c);
        _radio.HandleChangeProposed += (_, h) => _postToUi(() => HandleChangeProposed?.Invoke(this, h));
        _radio.TruckerUpdated += (_, _) => _postToUi(RefreshContacts);
        _audio.SetChannelVolume("cb", _aiSettings.RadioVolume);
        _audio.SetChannelVolume("dispatcher", _aiSettings.DispatcherVolume);
        _audio.SetChannelVolume("advisor", _aiSettings.AdvisorVolume);
        RefreshCareerViews(ComputeInsights());
        RefreshContacts();
    }

    private string? CoDriverPhrase(DrivingIncident incident)
    {
        var text = CoDriverVoice.Phrase(incident, _aiSettings.CoDriver);
        if (text is not null) _lastSpokenAdvice = text;
        return text;
    }

    private void OnTelemetryFeatures(AtsTelemetrySnapshot s, AtsTelemetrySnapshot? prev)
    {
        if (!s.IsLive) { _parkedSince = null; _radio.ParkedFor = TimeSpan.Zero; return; }

        // Safe Driver progress: real odometer miles, ignoring jumps (ferries, teleports).
        if (prev is { IsLive: true })
        {
            float d = s.OdometerMiles - prev.OdometerMiles;
            if (d is > 0 and < 5) AchievementManager.AddMiles(Career, d);
        }

        // Parked = stopped with the parking brake set; enables relaxed truck-stop talk.
        if (s.SpeedMph < 1 && s.ParkingBrake) _parkedSince ??= s.Timestamp;
        else _parkedSince = null;
        _radio.ParkedFor = _parkedSince is { } since ? s.Timestamp - since : TimeSpan.Zero;

        string truck = $"{s.TruckMake} {s.TruckModel}".Trim();
        if (truck.Length > 0 && truck != _lastTruck)
        {
            _lastTruck = truck;
            var insights = ComputeInsights();
            _postToUi(() => RefreshCareerViews(insights));
        }
    }

    private IReadOnlyList<AchievementItem> OnTripFinishedFeatures(DeliveryTripRecord trip)
    {
        lock (_tripsLock) _allTrips.Add(trip);
        LearnTripCity(trip, _telemetry.LatestSnapshot, atStart: false);
        SaveMapMemories();
        var unlocked = AchievementManager.Evaluate(Company, Career, trip, DateTimeOffset.UtcNow);
        _dispatcher?.OnTripFinished(trip);
        var insights = ComputeInsights();
        _postToUi(() => RefreshCareerViews(insights));
        return unlocked;
    }

    private IReadOnlyList<string> ComputeInsights()
    {
        List<DeliveryTripRecord> trips;
        lock (_tripsLock) trips = _allTrips.ToList();
        return InsightEngine.Generate(trips, Career, _lastTruck, DateTimeOffset.UtcNow);
    }

    private void RefreshCareerViews(IReadOnlyList<string> insights)
    {
        Achievements.Clear();
        foreach (var a in AchievementManager.List(Company, Career)) Achievements.Add(a);
        Insights.Clear();
        foreach (var i in insights) Insights.Add(i);
        TruckEconomyRows.Clear();
        foreach (var (truck, e) in Career.TruckEconomy.OrderByDescending(kv => kv.Value.Miles))
            TruckEconomyRows.Add($"{truck}: {Units.EconomyText(e.AverageMpg)} over {Units.DistText(e.Miles)} ({e.Trips} trips)");
        RefreshTrends();
        RaiseAll();
    }

    // ---- Contacts (spec §40E) ----

    public void RefreshContacts()
    {
        Contacts.Clear();
        foreach (var t in _radio.Roster.OrderByDescending(t => t.LastSpokenAt ?? DateTimeOffset.MinValue))
        {
            Contacts.Add(new ContactView(t.Id, t.Handle, t.FullName, t.Truck, t.HomeState, Spaced(t.Relationship.ToString()),
                t.LastSpokenAt is { } last ? Ago(DateTimeOffset.UtcNow - last) : "Never", t.ConversationCount, t.SummarizedMemories.ToList(),
                t.PersonalityTraits, string.Join(" • ", t.FavoriteTopics), t.CurrentFictionalRegion, t.YearsExperience,
                Math.Clamp(t.RelationshipPoints / 160.0 * 100.0, 0, 100)));
        }
    }

    /// <summary>Forgets what this trucker remembers about you (relationship level is kept).</summary>
    public void DeleteContactMemories(string truckerId)
    {
        var t = _radio.Roster.FirstOrDefault(x => x.Id == truckerId);
        if (t is null) return;
        t.SummarizedMemories.Clear();
        SafeDb(db => db.SaveTrucker(t));
        RefreshContacts();
    }

    /// <summary>Saves a handle the player introduced on the radio, after they confirmed it.</summary>
    public void ConfirmHandle(string handle)
    {
        Company.PlayerHandle = handle.Trim();
        SafeDb(db => db.SaveCompany(Company));
        Raise(nameof(CompanySummary));
        ShowNotice($"CB handle saved: {Company.PlayerHandle}");
    }

    // ---- Voice commands (spec §40F) ----

    private void HandleVoiceCommand(VoiceCommand c)
    {
        var s = _telemetry.LatestSnapshot;
        void Say(string text) => _audio.EnqueueAnnouncement(text, AudioPriority.Normal, "advisor");
        switch (c.Kind)
        {
            case VoiceCommandKind.SwitchChannel:
                Say($"Channel {c.Channel}, {ChannelName(c.Channel ?? 19).ToLowerInvariant()}.");
                _postToUi(() => Raise(nameof(RadioChannelText)));
                break;
            case VoiceCommandKind.CallDispatch:
                _radio.SwitchChannel(CBRadioService.DispatchChannel);
                _postToUi(() => Raise(nameof(RadioChannelText)));
                _ = Task.Run(() => _radio.SendDispatchMessageAsync("The driver is calling in. Answer and ask what they need.", important: true));
                break;
            case VoiceCommandKind.FuelReport:
                Say(!s.HasFuelCapacity ? "Fuel data isn't available right now."
                    : $"Fuel at {s.FuelPercent:F0} percent.{(s.EstimatedRangeMiles > 0 ? $" About {Units.Dist(s.EstimatedRangeMiles):F0} {Units.DistWord} of range." : "")}" +
                      (s.NavigationDistanceRemainingMiles > 0 && s.EstimatedRangeMiles > 0
                          ? (s.EstimatedRangeMiles >= s.NavigationDistanceRemainingMiles * 1.15f ? " Enough to reach your destination at this rate." : " Not enough to reach your destination; plan a fuel stop.")
                          : ""));
                break;
            case VoiceCommandKind.DistanceReport:
                Say(s.NavigationDistanceRemainingMiles > 0
                    ? $"{Units.Dist(s.NavigationDistanceRemainingMiles):F0} {Units.DistWord} to {(string.IsNullOrEmpty(s.DestinationCity) ? "your destination" : s.DestinationCity)}" +
                      (s.NavigationTimeRemainingMinutes > 0 ? $", about {(int)(s.NavigationTimeRemainingMinutes / 60)} hours {(int)s.NavigationTimeRemainingMinutes % 60} minutes by the navigation estimate." : ".")
                    : "No route is set in the game's navigation right now.");
                break;
            case VoiceCommandKind.VolumeDown:
            case VoiceCommandKind.VolumeUp:
                float step = c.Kind == VoiceCommandKind.VolumeUp ? 0.15f : -0.15f;
                _aiSettings.RadioVolume = Math.Clamp(_aiSettings.RadioVolume + step, 0.05f, 1f);
                _audio.SetChannelVolume("cb", _aiSettings.RadioVolume);
                Say($"Radio volume {_aiSettings.RadioVolume * 100:F0} percent.");
                break;
            case VoiceCommandKind.Mute:
                _postToUi(() => MuteRequested?.Invoke(this, true));
                break;
            case VoiceCommandKind.Unmute:
                _postToUi(() => MuteRequested?.Invoke(this, false));
                Say("Audio back on.");
                break;
            case VoiceCommandKind.RepeatLast:
                Say(_lastSpokenAdvice ?? "Nothing to repeat yet.");
                break;
            default:
                Say("Sorry, I didn't catch that command. Try: switch to channel nineteen, call dispatch, fuel range, or how far to go.");
                break;
        }
    }

    private static string Spaced(string pascal) =>
        string.Concat(pascal.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString()));

    private static string Ago(TimeSpan span) => span.TotalMinutes < 2 ? "Just now"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
        : span.TotalDays < 2 ? "Yesterday"
        : $"{(int)span.TotalDays} days ago";
}
