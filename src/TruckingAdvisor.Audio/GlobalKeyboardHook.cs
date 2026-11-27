using System.Diagnostics;
using System.Runtime.InteropServices;
using TruckingAdvisor.Shared.Diagnostics;

namespace TruckingAdvisor.Audio;

/// <summary>
/// Low-level keyboard hook (WH_KEYBOARD_LL): reports every key press and release, even while
/// American Truck Simulator has focus. Keys are never swallowed, so the game still sees them.
/// What a key does is decided by the InputRouter, not here.
///
/// The hook lives on its own background thread with its own message loop. Windows calls a
/// low-level hook on the thread that installed it, and every keystroke in the system waits for it;
/// on the UI thread a busy window would delay keys in the game, and Windows silently removes a hook
/// that keeps timing out. <see cref="KeyDown"/>/<see cref="KeyUp"/> are raised on that thread, so
/// handlers must be quick and marshal to the UI themselves.
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012, PM_NOREMOVE = 0x0000;

    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc;
    private readonly object _sync = new();
    private Thread? _thread;
    private volatile uint _threadId;

    /// <summary>Virtual-key code pressed (auto-repeat included; the router filters it).</summary>
    public event Action<int>? KeyDown;
    public event Action<int>? KeyUp;

    public GlobalKeyboardHook() => _proc = HookCallback;

    /// <summary>Starts the hook thread and waits (briefly) until the hook is in place.</summary>
    public void Install()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var ready = new ManualResetEventSlim();
        lock (_sync)
        {
            if (_thread is not null) return;
            _thread = new Thread(() => HookThread(ready)) { IsBackground = true, Name = "Keyboard hook" };
            _thread.Start();
        }
        if (!ready.Wait(TimeSpan.FromSeconds(5))) AppLog.Warn("Keyboard hook: the hook thread didn't start in time");
    }

    private void HookThread(ManualResetEventSlim ready)
    {
        try
        {
            // Create this thread's message queue before anyone can post WM_QUIT to it.
            PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
            _threadId = GetCurrentThreadId();

            IntPtr module = IntPtr.Zero;
            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                if (curModule is not null) module = GetModuleHandle(curModule.ModuleName);
            }
            catch { /* fall back to the exe's handle below */ }
            if (module == IntPtr.Zero) module = GetModuleHandle(null);

            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, module, 0);
            if (_hookId == IntPtr.Zero)
            {
                AppLog.Error($"Keyboard hook could not be installed (Win32 error {Marshal.GetLastWin32Error()}); keyboard push-to-talk won't work outside this window");
                return;
            }
        }
        finally
        {
            try { ready.Set(); } catch (ObjectDisposedException) { }
        }

        // Windows delivers the hook callbacks through this loop. WM_QUIT (from Dispose) ends it.
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
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

    /// <summary>Removes the hook and ends its thread.</summary>
    public void Dispose()
    {
        Thread? thread;
        lock (_sync)
        {
            thread = _thread;
            _thread = null;
        }
        if (thread is null || !OperatingSystem.IsWindows()) return;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (!thread.Join(TimeSpan.FromSeconds(2))) AppLog.Warn("Keyboard hook: the hook thread didn't stop in time");
        _threadId = 0;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
