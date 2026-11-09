using System.Globalization;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.AI;

/// <summary>
/// Decides when the company dispatcher calls the driver, using only game data:
/// job briefing, routine check-ins, approaching the destination, low fuel vs. distance left,
/// rest due, new damage, deadline risk, and delivery wrap-up.
///
/// The rules are deterministic; AI (when on) only words the message. Messages wait in a short queue
/// until the radio is free and expire if they can't be delivered in time.
/// Call <see cref="Evaluate"/> about once a second from any thread.
/// </summary>
public sealed class DispatcherService
{
    public delegate Task<bool> SendMessage(string facts, bool important, CancellationToken ct);

    private sealed record Pending(string Key, string Facts, bool Important, DateTimeOffset Created, int TripGeneration);

    private readonly AiSettings _settings;
    private readonly SendMessage _send;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();
    private readonly List<Pending> _pending = new();
    /// <summary>One-time notices actually delivered this trip (not just queued).</summary>
    private readonly HashSet<string> _deliveredThisTrip = new();
    private int _tripGeneration;
    private DateTimeOffset _lastContact = DateTimeOffset.MinValue;
    private float _damageAtLastNotice;   // only advances once a damage notice is actually delivered
    private float _damagePendingValue;
    private bool _restNoticeArmed = true;
    private int _sending;

    public DispatcherService(AiSettings settings, SendMessage send, Func<DateTimeOffset>? clock = null)
    {
        _settings = settings;
        _send = send;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Minutes between routine check-ins for the current contact level (0 = none).</summary>
    public int CheckInMinutes => _settings.DispatchContact switch
    {
        DispatchContactLevel.Rare => 60,
        DispatchContactLevel.Normal => 30,
        DispatchContactLevel.Frequent => 15,
        _ => 0
    };

    public void OnTripStarted(DeliveryTripRecord trip)
    {
        lock (_sync)
        {
            _tripGeneration++;
            _deliveredThisTrip.Clear();
            _pending.RemoveAll(p => p.Key != "wrapup"); // nothing stale from a trip that ended without a wrap-up
            _damageAtLastNotice = 0;
            _lastContact = _clock();
        }
        if (trip.StartedMidDelivery) return; // no "new load" briefing for a job already underway
        var facts = new List<string> { $"New load assigned: {Or(trip.Cargo, "freight")}" };
        if (trip.CargoWeightLbs > 0) facts.Add($"about {trip.CargoWeightLbs:N0} lbs");
        if (!string.IsNullOrEmpty(trip.Destination)) facts.Add($"from {Or(trip.Origin, "the pickup")} to {trip.Destination}");
        if (trip.PlannedDistanceMiles > 0) facts.Add($"{trip.PlannedDistanceMiles:N0} miles planned");
        Queue("briefing", string.Join(", ", facts) + ". Wish them a safe run.", important: false);
    }

    public void OnTripFinished(DeliveryTripRecord trip)
    {
        string dest = Or(trip.Destination, "the receiver");
        string facts = trip.State == DeliveryState.Completed
            ? $"Load delivered to {dest}. TruckingAdvisor driver score {trip.ScoreBreakdown.FinalScore} ({trip.ScoreBreakdown.RatingLabel})" +
              (trip.AddedTruckDamagePercent < 0.5f && trip.AddedTrailerDamagePercent < 0.5f ? ", no damage" : "") +
              (trip.DeliveredOnTime == false ? ", but it arrived after the deadline" : "") + ". Thank them."
            : $"The job to {dest} ended without a delivery. Acknowledge it briefly and kindly.";
        lock (_sync)
        {
            _tripGeneration++;
            _pending.RemoveAll(p => p.Key != "wrapup");
        }
        Queue("wrapup", facts, important: false);
    }

    public void Evaluate(AtsTelemetrySnapshot s, DeliveryTripRecord? trip)
    {
        if (_settings.DispatchContact == DispatchContactLevel.Off) return;
        if (trip is not null && s.IsLive && !s.IsPaused)
        {
            var now = _clock();
            string dest = Or(trip.Destination, "the destination");
            float remaining = s.NavigationDistanceRemainingMiles;

            // Approaching the destination.
            if (remaining is > 0 and <= 15)
                Once("approaching", $"The driver is about {remaining:F0} miles from {dest}. Encourage them on the final stretch.", false);

            // Low fuel relative to the distance left.
            if (s.HasFuelCapacity && s.FuelPercent <= 20 && remaining > 0 &&
                (s.EstimatedRangeMiles <= 0 || s.EstimatedRangeMiles < remaining * 1.1f))
            {
                string range = s.EstimatedRangeMiles > 0 ? $", about {s.EstimatedRangeMiles:F0} miles of range" : "";
                Once("fuel", $"Fuel is at {s.FuelPercent:F0}%{range}, with {remaining:F0} miles to {dest}. Suggest planning a fuel stop.", false);
            }

            // Rest (re-arms after the driver has rested).
            if (s.HasRestData)
            {
                if (s.NextRestStopGameMinutes > 120) _restNoticeArmed = true;
                else if (s.NextRestStopGameMinutes <= 60 && _restNoticeArmed)
                {
                    _restNoticeArmed = false;
                    Queue("rest", $"The driver needs to rest within about {s.NextRestStopGameMinutes} minutes of game time. Suggest finding a place to shut down.", false);
                }
            }

            // New damage during this trip.
            lock (_sync)
            {
                if (trip.AddedTruckDamagePercent < _damageAtLastNotice) _damageAtLastNotice = trip.AddedTruckDamagePercent; // repaired
                if (trip.AddedTruckDamagePercent - _damageAtLastNotice >= 3f)
                {
                    _damagePendingValue = trip.AddedTruckDamagePercent;
                    Queue("damage", $"The truck has taken about {trip.AddedTruckDamagePercent:F0}% new damage on this trip. Ask if everything is OK; suggest a repair shop if needed.", true);
                }
            }

            // Arrival estimate after the deadline (navigation time is in game time).
            if (s.JobDeadlineGameMinutes > 0 && s.GameTimeMinutes > 0 && s.NavigationTimeRemainingMinutes > 0 &&
                s.GameTimeMinutes + s.NavigationTimeRemainingMinutes > s.JobDeadlineGameMinutes)
            {
                Once("deadline", $"The navigation estimate puts arrival at {dest} after the delivery deadline ({FormatGameTime(s.JobDeadlineGameMinutes)}). Let them know calmly; never tell them to speed or skip rest.", false);
            }

            // Routine check-in.
            if (CheckInMinutes > 0 && s.SpeedMph > 5 && now - _lastContact >= TimeSpan.FromMinutes(CheckInMinutes))
            {
                string eta = s.NavigationTimeRemainingMinutes > 0
                    ? $", about {(int)(s.NavigationTimeRemainingMinutes / 60)}h {(int)s.NavigationTimeRemainingMinutes % 60:D2}m by the navigation estimate"
                    : "";
                string left = remaining > 0 ? $"{remaining:F0} miles left to {dest}{eta}" : $"still headed to {dest}";
                Queue("checkin", $"Routine check-in: {left}. Ask how the truck is running.", false);
                lock (_sync) _lastContact = now; // even if it ends up dropped, don't nag every second
            }
        }
        TrySendNext();
    }

    /// <summary>Messages waiting for the radio (for tests and diagnostics).</summary>
    public IReadOnlyList<string> PendingKeys { get { lock (_sync) return _pending.Select(p => p.Key).ToList(); } }

    /// <summary>
    /// Queues a notice that should be delivered once per trip. It only counts as done once it's
    /// actually delivered; if it expires while the radio is busy, it's queued again next time the
    /// condition still holds.
    /// </summary>
    private void Once(string key, string facts, bool important)
    {
        lock (_sync)
        {
            if (_deliveredThisTrip.Contains(key) || _pending.Any(p => p.Key == key)) return;
        }
        Queue(key, facts, important);
    }

    private void Queue(string key, string facts, bool important)
    {
        if (_settings.DispatchContact == DispatchContactLevel.Off) return;
        lock (_sync)
        {
            _pending.RemoveAll(p => p.Key == key);
            _pending.Add(new Pending(key, facts, important, _clock(), _tripGeneration));
        }
    }

    private void TrySendNext()
    {
        Pending? next;
        lock (_sync)
        {
            var now = _clock();
            _pending.RemoveAll(p => now - p.Created > (p.Important ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(3)));
            // Messages about an earlier trip are stale once a new trip has started (the wrap-up is the exception).
            _pending.RemoveAll(p => p.TripGeneration != _tripGeneration && p.Key != "wrapup");
            next = _pending.OrderByDescending(p => p.Important).ThenBy(p => p.Created).FirstOrDefault();
        }
        if (next is null || Interlocked.CompareExchange(ref _sending, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (await _send(next.Facts, next.Important, CancellationToken.None))
                {
                    lock (_sync)
                    {
                        _pending.Remove(next);
                        _lastContact = _clock();
                        // A message from an earlier trip must not count against the current one.
                        if (next.TripGeneration == _tripGeneration) _deliveredThisTrip.Add(next.Key);
                        if (next.Key == "damage") _damageAtLastNotice = Math.Max(_damageAtLastNotice, _damagePendingValue);
                    }
                }
            }
            catch { /* radio problems must never break dispatch logic */ }
            finally { Interlocked.Exchange(ref _sending, 0); }
        });
    }

    /// <summary>Formats absolute game minutes as "Wed 14:30" (game day 0 is a Monday).</summary>
    public static string FormatGameTime(uint minutes)
    {
        string[] days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        uint day = minutes / 1440 % 7;
        uint t = minutes % 1440;
        return string.Create(CultureInfo.InvariantCulture, $"{days[day]} {t / 60:D2}:{t % 60:D2}");
    }

    private static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;
}
