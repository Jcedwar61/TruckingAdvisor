using TruckingAdvisor.Shared.Enums;

namespace TruckingAdvisor.Presentation;

/// <summary>One step of the getting-started checklist.</summary>
public sealed record SetupStep(string Text, bool Done, string Hint)
{
    public string Mark => Done ? "✓" : "○";
}

/// <summary>
/// Getting-started checklist on the dashboard: game found, plugin installed, live data,
/// first CB call, first delivery. Hides itself once everything is done (or when closed).
/// </summary>
public sealed partial class MainDashboardViewModel
{
    private bool _gameFound, _pluginInstalled;

    public void SetSetupStatus(bool gameFound, bool pluginInstalled)
    {
        _gameFound = gameFound;
        _pluginInstalled = pluginInstalled;
        Raise(nameof(SetupSteps));
        Raise(nameof(ShowSetupChecklist));
    }

    public IReadOnlyList<SetupStep> SetupSteps => new[]
    {
        new SetupStep("Find the game", _gameFound, "Found automatically on any drive, or pick the folder in More > Install Telemetry Plugin."),
        new SetupStep("Install the telemetry plugin", _pluginInstalled, "More > Install Telemetry Plugin (backs up anything it replaces)."),
        new SetupStep("Get live data", _current.IsLive, "Start ATS or ETS2 and drive off; the header turns green."),
        new SetupStep("Make your first CB call", RadioMessages.Any(m => m.IsMine), "Hold push-to-talk (Left Alt) or type in the radio box: \"Breaker one-nine...\""),
        new SetupStep("Finish a delivery", Company.TotalDeliveries > 0, "Take any job; your score, pay and route are recorded."),
    };

    public bool ShowSetupChecklist => !_aiSettings.SetupChecklistDismissed && !_current.IsDemoData && SetupSteps.Any(s => !s.Done);

    public void DismissSetupChecklist()
    {
        _aiSettings.SetupChecklistDismissed = true;
        try { _aiSettings.Save(); } catch { }
        Raise(nameof(ShowSetupChecklist));
    }
}
