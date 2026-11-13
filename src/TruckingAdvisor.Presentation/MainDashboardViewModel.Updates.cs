namespace TruckingAdvisor.Presentation;

/// <summary>Update banner: shown once an update is downloaded and verified; never spoken aloud.</summary>
public sealed partial class MainDashboardViewModel
{
    private bool _updateVisible;
    private string _updateTitle = "", _updateDetail = "";

    public bool IsUpdateVisible { get => _updateVisible; private set { _updateVisible = value; Raise(); } }
    public string UpdateTitle { get => _updateTitle; private set { _updateTitle = value; Raise(); } }
    public string UpdateDetail { get => _updateDetail; private set { _updateDetail = value; Raise(); } }

    public void ShowUpdateReady(string version, bool installsOnExit)
    {
        UpdateTitle = $"TruckingAdvisor {version} is ready to install";
        UpdateDetail = installsOnExit
            ? "Restart now, or keep driving: it installs by itself when you close the app."
            : "Restart to install it. Your trips, truckers and settings are kept.";
        IsUpdateVisible = true;
    }

    public void HideUpdate() => IsUpdateVisible = false;
}
