using TruckingAdvisor.Shared.Input;
using Vortice.DirectInput;

namespace TruckingAdvisor.Audio;

/// <summary>
/// Reads buttons and D-pads from wheels, button boxes, joysticks and gamepads through DirectInput,
/// in the background (non-exclusive), so it works while the game has focus and never takes the
/// device away from the game. Polls about 50 times a second; devices can be plugged in or out at any time.
/// </summary>
public sealed class GameControllerInput : IDisposable
{
    private sealed class Pad
    {
        public required IDirectInputDevice8 Device;
        public required string Id;
        public required string Name;
        public bool[] Buttons = Array.Empty<bool>();
        public bool[] Pov = Array.Empty<bool>();
    }

    private readonly IntPtr _windowHandle;
    private readonly List<Pad> _pads = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    /// <summary>Raised on the polling thread; marshal to the UI thread before touching UI.</summary>
    public event Action<InputBinding>? ButtonDown;
    public event Action<InputBinding>? ButtonUp;
    /// <summary>Connected controllers changed (names), raised on the polling thread.</summary>
    public event Action<IReadOnlyList<string>>? DevicesChanged;
    /// <summary>Why controllers can't be read (e.g. DirectInput unavailable), raised once.</summary>
    public event Action<string>? Error;

    public IReadOnlyList<string> DeviceNames
    {
        get { lock (_lock) return _pads.Select(p => p.Name).ToList(); }
    }

    /// <param name="windowHandle">A top-level window of this app (DirectInput needs one for background access).</param>
    public GameControllerInput(IntPtr windowHandle) => _windowHandle = windowHandle;

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "Game controller input" };
        _thread.Start();
    }

    private void Run(CancellationToken token)
    {
        IDirectInput8 di;
        try { di = DInput.DirectInput8Create(); }
        catch (Exception ex)
        {
            Error?.Invoke($"Wheel/button-box input is unavailable: {ex.Message}");
            return;
        }
        using (di)
        {
            var nextScan = DateTime.MinValue;
            while (!token.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= nextScan)
                {
                    Scan(di);
                    nextScan = DateTime.UtcNow.AddSeconds(3);
                }
                PollAll();
                token.WaitHandle.WaitOne(20);
            }
            lock (_lock)
            {
                foreach (var p in _pads) Release(p);
                _pads.Clear();
            }
        }
    }

    /// <summary>Opens newly attached controllers.</summary>
    private void Scan(IDirectInput8 di)
    {
        bool changed = false;
        try
        {
            foreach (var info in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
            {
                string id = info.InstanceGuid.ToString();
                lock (_lock) if (_pads.Any(p => p.Id == id)) continue;
                try
                {
                    var dev = di.CreateDevice(info.InstanceGuid);
                    dev.SetCooperativeLevel(_windowHandle, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                    dev.SetDataFormat<RawJoystickState>();
                    dev.Acquire();
                    string name = string.IsNullOrWhiteSpace(info.ProductName) ? info.InstanceName : info.ProductName;
                    lock (_lock) _pads.Add(new Pad { Device = dev, Id = id, Name = name.Trim() });
                    changed = true;
                }
                catch { /* busy or unsupported device: try again on the next scan */ }
            }
        }
        catch { }
        if (changed) DevicesChanged?.Invoke(DeviceNames);
    }

    private void PollAll()
    {
        List<Pad> pads;
        lock (_lock) pads = _pads.ToList();
        foreach (var pad in pads)
        {
            JoystickState state;
            try
            {
                pad.Device.Poll();
                state = pad.Device.GetCurrentJoystickState();
            }
            catch
            {
                // Unplugged: release anything held, forget it, and pick it up again on a later scan.
                lock (_lock) _pads.Remove(pad);
                Release(pad);
                DevicesChanged?.Invoke(DeviceNames);
                continue;
            }

            var buttons = state.Buttons ?? Array.Empty<bool>();
            if (pad.Buttons.Length != buttons.Length) pad.Buttons = new bool[buttons.Length];
            for (int i = 0; i < buttons.Length; i++)
                Edge(pad, i, ref pad.Buttons[i], buttons[i]);

            var povs = state.PointOfViewControllers ?? Array.Empty<int>();
            if (pad.Pov.Length != povs.Length * 4) pad.Pov = new bool[povs.Length * 4];
            for (int h = 0; h < povs.Length; h++)
            {
                var dirs = InputBinding.PovDirections(povs[h]);
                for (int d = 0; d < 4; d++)
                    Edge(pad, InputBinding.PovBase + h * 4 + d, ref pad.Pov[h * 4 + d], dirs[d]);
            }
        }
    }

    private void Edge(Pad pad, int code, ref bool was, bool now)
    {
        if (was == now) return;
        was = now;
        var binding = new InputBinding(InputSource.Controller, code, pad.Id, pad.Name);
        if (now) ButtonDown?.Invoke(binding);
        else ButtonUp?.Invoke(binding);
    }

    private void Release(Pad pad)
    {
        for (int i = 0; i < pad.Buttons.Length; i++) if (pad.Buttons[i]) Edge(pad, i, ref pad.Buttons[i], false);
        for (int i = 0; i < pad.Pov.Length; i++) if (pad.Pov[i]) Edge(pad, InputBinding.PovBase + i, ref pad.Pov[i], false);
        try { pad.Device.Unacquire(); } catch { }
        try { pad.Device.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _thread?.Join(500);
        _thread = null;
    }
}
