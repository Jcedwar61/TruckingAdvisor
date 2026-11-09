using System.Collections.ObjectModel;
using TruckingAdvisor.AI;
using TruckingAdvisor.Core.Career;
using TruckingAdvisor.Core.Coaching;
using TruckingAdvisor.Core.Insights;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Presentation;

/// <summary>One CB contact for the Contacts page.</summary>
public sealed record ContactView(string Id, string Handle, string Name, string Truck, string HomeState, string Relationship,
    string LastContact, int Conversations, IReadOnlyList<string> Memories);

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

    public ObservableCollection<AchievementItem> Achievements { get; } = new();
    public ObservableCollection<string> Insights { get; } = new();
    public ObservableCollection<ContactView> Contacts { get; } = new();
    public ObservableCollection<string> TruckEconomyRows { get; } = new();

    public TimeSpan SessionDuration => DateTimeOffset.UtcNow - _sessionStart;

    public string CareerSummary =>
        $"Level {Career.Level} {Career.RankTitle} • {Career.CurrentXp:N0}/{Career.NextLevelXp:N0} XP • best score {Career.PersonalBestScore} • damage-free streak {Career.CurrentDamageFreeStreak} (best {Career.BestDamageFreeStreak})";

    public string CompanySummary =>
        $"{Company.CompanyName}{(string.IsNullOrWhiteSpace(Company.Headquarters) ? "" : $" • {Company.Headquarters}")} • driver {Company.DisplayHandle}\n" +
        $"{Company.TotalDeliveries} deliveries • {Company.TotalMilesDriven:N0} mi • {(Company.TotalDeliveries > 0 ? 100.0 * Company.DamageFreeDeliveries / Company.TotalDeliveries : 0):F0}% damage-free • " +
        $"avg score {Company.AverageDriverRating:F0} • {(Company.LifetimeAverageMpg > 0 ? $"{Company.LifetimeAverageMpg:F1} MPG" : "MPG --")} • income {Company.RecordedRevenue:N0} (in-game) • {Company.VisitedCities.Count} cities";

    /// <summary>Raised when a voice command asks to mute/unmute (the UI owns the speaker).</summary>
    public event EventHandler<bool>? MuteRequested;
    /// <summary>The player introduced a new handle; the UI should ask before saving it.</summary>
    public event EventHandler<string>? HandleChangeProposed;

    /// <summary>Connects the dispatcher; null disables proactive dispatch.</summary>
    public void AttachDispatcher(DispatcherService dispatcher) => _dispatcher = dispatcher;

    private void InitFeatures()
    {
        lock (_tripsLock) _allTrips.AddRange(TripHistory);
        _trips.StateChanged += (_, state) =>
        {
            if (state == DeliveryState.JobStarted && _trips.ActiveTrip is { } t)
            {
                _dispatcher?.OnTripStarted(t);
                // One personal insight at the start of a load, low priority.
                var insight = ComputeInsights().FirstOrDefault();
                if (insight is not null) _audio.EnqueueAnnouncement(insight, AudioPriority.Low, "advisor");
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
            TruckEconomyRows.Add($"{truck}: {e.AverageMpg:F1} MPG over {e.Miles:N0} mi ({e.Trips} trips)");
        Raise(nameof(CareerSummary));
        Raise(nameof(CompanySummary));
    }

    // ---- Contacts (spec §40E) ----

    public void RefreshContacts()
    {
        Contacts.Clear();
        foreach (var t in _radio.Roster.OrderByDescending(t => t.LastSpokenAt ?? DateTimeOffset.MinValue))
        {
            Contacts.Add(new ContactView(t.Id, t.Handle, t.FullName, t.Truck, t.HomeState, Spaced(t.Relationship.ToString()),
                t.LastSpokenAt is { } last ? Ago(DateTimeOffset.UtcNow - last) : "Never", t.ConversationCount, t.SummarizedMemories.ToList()));
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
                    : $"Fuel at {s.FuelPercent:F0} percent.{(s.EstimatedRangeMiles > 0 ? $" About {s.EstimatedRangeMiles:F0} miles of range." : "")}" +
                      (s.NavigationDistanceRemainingMiles > 0 && s.EstimatedRangeMiles > 0
                          ? (s.EstimatedRangeMiles >= s.NavigationDistanceRemainingMiles * 1.15f ? " Enough to reach your destination at this rate." : " Not enough to reach your destination; plan a fuel stop.")
                          : ""));
                break;
            case VoiceCommandKind.DistanceReport:
                Say(s.NavigationDistanceRemainingMiles > 0
                    ? $"{s.NavigationDistanceRemainingMiles:F0} miles to {(string.IsNullOrEmpty(s.DestinationCity) ? "your destination" : s.DestinationCity)}" +
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
