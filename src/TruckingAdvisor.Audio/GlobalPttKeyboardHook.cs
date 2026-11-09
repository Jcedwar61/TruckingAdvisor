using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TruckingAdvisor.Audio;

public sealed class ActionKeyBindingConfig
{
    public string ActionId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int VirtualKeyCode { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Installs a low-level Win32 keyboard hook (WH_KEYBOARD_LL) so Push-To-Talk and all GUI-bound
/// TruckingAdvisor actions work seamlessly while American Truck Simulator has fullscreen focus.
/// </summary>
public sealed class GlobalPttKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    public int PttVirtualKeyCode { get; set; } = 0xA4; // VK_LMENU (Left Alt)
    public bool PttHoldMode { get; set; } = true;

    private readonly Dictionary<string, int> _actionKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pushToTalk"] = 0xA4,         // Left Alt
        ["toggleOverlay"] = 0x77,      // F8
        ["repeatAdvisory"] = 0x78,     // F9
        ["cycleCbChannel"] = 0xDD,     // ]
        ["quickDispatchCheck"] = 0x79, // F10
        ["quickFuelReport"] = 0x76,    // F7
        ["acknowledgeAlert"] = 0xDC,   // \
        ["emergencyMute"] = 0x7A       // F11
    };

    private IntPtr _hookId = IntPtr.Zero;
    private bool _isPttKeyDown;
    private bool _isPttLatched;
    private readonly LowLevelKeyboardProc _proc;

    public event EventHandler? PttPressed;
    public event EventHandler? PttReleased;
    public event EventHandler<string>? ActionTriggered;

    public IReadOnlyDictionary<string, int> ActionKeyMap => _actionKeyMap;

    public GlobalPttKeyboardHook()
    {
        _proc = HookCallback;
    }

    public void BindKey(string actionId, int virtualKeyCode)
    {
        _actionKeyMap[actionId] = virtualKeyCode;
        if (string.Equals(actionId, "pushToTalk", StringComparison.OrdinalIgnoreCase))
        {
            PttVirtualKeyCode = virtualKeyCode;
        }
    }

    public void SaveBindings(string filePath)
    {
        var json = JsonSerializer.Serialize(_actionKeyMap, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    public void LoadBindings(string filePath)
    {
        if (!File.Exists(filePath)) return;
        var loaded = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(filePath));
        if (loaded is null) return;
        foreach (var kv in loaded)
        {
            BindKey(kv.Key, kv.Value);
        }
    }

    public void Install()
    {
        if (!OperatingSystem.IsWindows() || _hookId != IntPtr.Zero) return;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        if (curModule is not null)
        {
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            bool isDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
            bool isUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

            if (vkCode == PttVirtualKeyCode)
            {
                if (PttHoldMode)
                {
                    if (isDown && !_isPttKeyDown)
                    {
                        _isPttKeyDown = true;
                        PttPressed?.Invoke(this, EventArgs.Empty);
                    }
                    else if (isUp && _isPttKeyDown)
                    {
                        _isPttKeyDown = false;
                        PttReleased?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (isDown)
                {
                    _isPttLatched = !_isPttLatched;
                    if (_isPttLatched) PttPressed?.Invoke(this, EventArgs.Empty);
                    else PttReleased?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (isDown)
            {
                foreach (var kv in _actionKeyMap)
                {
                    if (!string.Equals(kv.Key, "pushToTalk", StringComparison.OrdinalIgnoreCase) && kv.Value == vkCode)
                    {
                        ActionTriggered?.Invoke(this, kv.Key);
                        break;
                    }
                }
            }
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
