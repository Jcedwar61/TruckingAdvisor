using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TruckingAdvisor.Audio;

/// <summary>
/// Low-level keyboard hook (WH_KEYBOARD_LL): reports every key press and release, even while
/// American Truck Simulator has focus. Keys are never swallowed, so the game still sees them.
/// What a key does is decided by the InputRouter, not here.
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105;

    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc;

    /// <summary>Virtual-key code pressed (auto-repeat included; the router filters it).</summary>
    public event Action<int>? KeyDown;
    public event Action<int>? KeyUp;

    public GlobalKeyboardHook() => _proc = HookCallback;

    public void Install()
    {
        if (!OperatingSystem.IsWindows() || _hookId != IntPtr.Zero) return;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        if (curModule is not null)
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);
            try
            {
                if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN) KeyDown?.Invoke(vk);
                else if (wParam == WM_KEYUP || wParam == WM_SYSKEYUP) KeyUp?.Invoke(vk);
            }
            catch { /* never let a handler break the hook chain */ }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
