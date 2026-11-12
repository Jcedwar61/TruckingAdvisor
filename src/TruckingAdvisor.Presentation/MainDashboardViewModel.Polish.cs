using System.Collections.ObjectModel;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Presentation;

/// <summary>One line on the radio, shown as a chat bubble (yours on the right, everyone else on the left).</summary>
public sealed record RadioMessage(string Handle, string Text, int Channel, bool IsMine, bool IsDispatch, DateTime Time)
{
    public string Header => $"{Handle} · CH {Channel} · {Time:HH:mm}";
}

/// <summary>
/// Calm extras: a one-click quiet period for the radio, a self-dismissing delivery banner,
/// and chat-style radio messages.
/// </summary>
public sealed partial class MainDashboardViewModel
{
    public static readonly TimeSpan QuietPeriodLength = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan BannerTime = TimeSpan.FromSeconds(20);

    public ObservableCollection<RadioMessage> RadioMessages { get; } = new();

    private void AddRadioMessage((string Speaker, string Handle, string Transcript, int Channel) e)
    {
        bool mine = string.Equals(e.Speaker, Company.DisplayHandle, StringComparison.OrdinalIgnoreCase);
        RadioMessages.Add(new RadioMessage(e.Handle, e.Transcript, e.Channel, mine, e.Channel == 30 && !mine, DateTime.Now));
        while (RadioMessages.Count > 200) RadioMessages.RemoveAt(0);
    }

    /// <summary>Number on the HUD's speed-limit sign ("--" when the game reports no limit).</summary>
    public string SpeedLimitNumber => HasData && _current.SpeedLimitMph > 0 ? $"{_current.SpeedLimitMph:F0}" : "--";

    /// <summary>The CB channel the radio is tuned to.</summary>
    public int ActiveChannel => _radio.ActiveChannel;

    // ---- Quiet period ----

    /// <summary>True while nobody will call you unprompted (safety warnings and your own calls still work).</summary>
    public bool IsQuiet => _radio.IsQuietPeriod;

    public string QuietButtonText => _radio.IsQuietPeriod && _radio.QuietUntil is { } until
        ? $"Quiet until {until.ToLocalTime():HH:mm} — tap to end"
        : "Quiet 30 min";

    /// <summary>Starts a 30-minute quiet period, or ends the current one early.</summary>
    public void ToggleQuiet()
    {
        if (_radio.IsQuietPeriod)
        {
            _radio.QuietUntil = null;
            RadioNotice = "Quiet period ended. The radio is back to normal.";
        }
        else
        {
            _radio.QuietUntil = _radio.Clock() + QuietPeriodLength;
            _radio.CancelChatter();
            RadioNotice = "Quiet for 30 minutes: no chatter or routine check-ins. Safety warnings and your own calls still work.";
        }
        Raise(nameof(IsQuiet));
        Raise(nameof(QuietButtonText));
        // Refresh the button when the period runs out on its own.
        if (_radio.QuietUntil is { } end)
        {
            var wait = end - _radio.Clock() + TimeSpan.FromSeconds(1);
            _ = Task.Delay(wait, _cts.Token).ContinueWith(_ => _postToUi(() =>
            {
                Raise(nameof(IsQuiet));
                Raise(nameof(QuietButtonText));
            }), TaskContinuationOptions.OnlyOnRanToCompletion);
        }
    }

    // ---- Delivery banner ----

    private string _bannerTitle = "", _bannerDetail = "";
    private bool _bannerVisible;
    private int _bannerVersion;

    public bool IsBannerVisible { get => _bannerVisible; private set { _bannerVisible = value; Raise(); } }
    public string BannerTitle { get => _bannerTitle; private set { _bannerTitle = value; Raise(); } }
    public string BannerDetail { get => _bannerDetail; private set { _bannerDetail = value; Raise(); } }
    public bool BannerIsGood { get; private set; }

    /// <summary>Shows a short summary of a finished delivery; it hides itself after <see cref="BannerTime"/>.</summary>
    public void ShowDeliveryBanner(DeliveryTripRecord trip, int levelsGained, IReadOnlyCollection<string> achievements)
    {
        if (trip.State == DeliveryState.Completed)
        {
            BannerTitle = $"Delivered: {trip.Cargo} to {Fallback(trip.Destination, "destination")} — score {trip.ScoreBreakdown.FinalScore} ({trip.ScoreBreakdown.RatingLabel})";
            var parts = new List<string> { $"{trip.DistanceMiles:F0} mi" };
            if (trip.AverageMpg > 0) parts.Add($"{trip.AverageMpg:F1} MPG");
            if (levelsGained > 0) parts.Add($"career level {Career.Level}");
            parts.AddRange(achievements.Select(a => $"🏆 {a}"));
            BannerDetail = string.Join("  ·  ", parts);
            BannerIsGood = true;
        }
        else
        {
            BannerTitle = $"Job {trip.State.ToString().ToLowerInvariant()}: {trip.Cargo}";
            BannerDetail = "Recorded in your trip history.";
            BannerIsGood = false;
        }
        Raise(nameof(BannerIsGood));
        IsBannerVisible = true;
        int version = ++_bannerVersion;
        _ = Task.Delay(BannerTime, _cts.Token).ContinueWith(_ => _postToUi(() =>
        {
            if (version == _bannerVersion) IsBannerVisible = false;
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    public void DismissBanner()
    {
        _bannerVersion++;
        IsBannerVisible = false;
    }
}
