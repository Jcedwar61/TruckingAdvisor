using System.Runtime.InteropServices;

namespace TruckingAdvisor.UI.Overlays;

/// <summary>
/// Provides Win32 extended window styles (WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE)
/// so the always-on-top Gameplay Overlay never steals keyboard/steering focus from American Truck Simulator.
/// </summary>
public static class GameplayOverlayInterop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public static void SetClickThroughAndNoActivate(IntPtr hwnd, bool enableClickThrough)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE;
        if (enableClickThrough)
            exStyle |= WS_EX_TRANSPARENT;
        else
            exStyle &= ~WS_EX_TRANSPARENT;

        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }
}
