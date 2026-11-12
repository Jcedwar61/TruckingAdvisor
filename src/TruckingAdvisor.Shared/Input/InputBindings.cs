using System.Text.Json;
using System.Text.Json.Serialization;

namespace TruckingAdvisor.Shared.Input;

public enum InputSource { Keyboard, Controller }

/// <summary>
/// One physical input: a keyboard key (Windows virtual-key code) or a button on a game controller
/// (wheel, button box, joystick, gamepad). Controller buttons are 0-based; POV hat directions use
/// <see cref="PovBase"/> + hat*4 + direction (0 up, 1 right, 2 down, 3 left).
/// </summary>
public sealed record InputBinding(InputSource Source, int Code, string? DeviceId = null, string? DeviceName = null)
{
    public const int PovBase = 1000;

    /// <summary>True when two bindings mean the same physical input (device name is just a label).</summary>
    public bool SameInput(InputBinding other) =>
        Source == other.Source && Code == other.Code &&
        (Source == InputSource.Keyboard || string.Equals(DeviceId, other.DeviceId, StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public string Display => Source == InputSource.Keyboard
        ? KeyNames.Name(Code)
        : $"{(string.IsNullOrWhiteSpace(DeviceName) ? "Controller" : DeviceName)}: {ButtonName(Code)}";

    /// <summary>Hat angle (hundredths of a degree; -1 or 65535 = centered) to up/right/down/left. Diagonals press two.</summary>
    public static bool[] PovDirections(int value)
    {
        if (value < 0 || value >= 36000) return new bool[4];
        return new[]
        {
            value <= 6750 || value >= 29250,
            value >= 2250 && value <= 15750,
            value >= 11250 && value <= 24750,
            value >= 20250 && value <= 33750
        };
    }

    public static string ButtonName(int code)
    {
        if (code < PovBase) return $"Button {code + 1}";
        int pov = (code - PovBase) / 4, dir = (code - PovBase) % 4;
        string d = dir switch { 0 => "Up", 1 => "Right", 2 => "Down", _ => "Left" };
        return pov == 0 ? $"D-pad {d}" : $"Hat {pov + 1} {d}";
    }
}

/// <summary>Everything the player can bind, with a label and the default keyboard key.</summary>
public sealed record InputAction(string Id, string Label, string Category, int DefaultKey = 0, bool IsHold = false);

public static class InputActions
{
    public const string PushToTalk = "pushToTalk";

    public static readonly IReadOnlyList<InputAction> All = new InputAction[]
    {
        new(PushToTalk, "Push-to-talk (hold)", "CB radio", 0xA4, IsHold: true),       // Left Alt
        new("cycleCbChannel", "Next CB channel", "CB radio", 0xDD),                       // ]
        new("previousCbChannel", "Previous CB channel", "CB radio", 0xDB),                // [
        new("channel19", "Channel 19 - Highway traffic", "CB radio"),
        new("channel9", "Channel 9 - Emergency", "CB radio"),
        new("channel17", "Channel 17 - Regional traffic", "CB radio"),
        new("channel21", "Channel 21 - Casual conversation", "CB radio"),
        new("channel25", "Channel 25 - Truck stop talk", "CB radio"),
        new("channel30", "Channel 30 - Company dispatch", "CB radio"),
        new("quickDispatchCheck", "Check in with dispatch", "CB radio", 0x79),            // F10
        new("toggleQuiet", "Quiet 30 min on/off", "CB radio"),
        new("radioVolumeUp", "Radio volume up", "Audio"),
        new("radioVolumeDown", "Radio volume down", "Audio"),
        new("acknowledgeAlert", "Stop current announcement", "Audio", 0xDC),              // \
        new("emergencyMute", "Mute all audio on/off", "Audio", 0x7A),                     // F11
        new("repeatAdvisory", "Repeat last advice", "Co-driver", 0x78),                   // F9
        new("quickFuelReport", "Fuel report", "Co-driver", 0x76),                         // F7
        new("distanceReport", "Distance and ETA report", "Co-driver"),
        new("toggleOverlay", "HUD on/off", "HUD", 0x77),                                  // F8
        new("showChannelPicker", "Show channel picker on HUD", "HUD"),
    };

    public static InputAction? Find(string id) => All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Channel number for the direct "channelNN" actions, else null.</summary>
    public static int? DirectChannel(string actionId) =>
        actionId.StartsWith("channel", StringComparison.OrdinalIgnoreCase) && int.TryParse(actionId.AsSpan(7), out int ch) ? ch : null;
}

/// <summary>
/// The player's bindings: any number of keys and controller buttons per action.
/// One physical input belongs to at most one action (binding it elsewhere moves it).
/// </summary>
public sealed class InputBindingSet
{
    public Dictionary<string, List<InputBinding>> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static InputBindingSet Defaults()
    {
        var set = new InputBindingSet();
        foreach (var a in InputActions.All.Where(a => a.DefaultKey != 0))
            set.Bindings[a.Id] = new() { new InputBinding(InputSource.Keyboard, a.DefaultKey) };
        return set;
    }

    public IReadOnlyList<InputBinding> For(string actionId) =>
        Bindings.TryGetValue(actionId, out var list) ? list : Array.Empty<InputBinding>();

    /// <summary>The action this input triggers, if any.</summary>
    public string? ActionFor(InputBinding input)
    {
        foreach (var (action, list) in Bindings)
            if (list.Any(b => b.SameInput(input))) return action;
        return null;
    }

    /// <summary>Adds the input to the action. Returns the action it was taken from, if it was bound elsewhere.</summary>
    public string? Bind(string actionId, InputBinding input)
    {
        string? previous = null;
        foreach (var (action, list) in Bindings)
            if (list.RemoveAll(b => b.SameInput(input)) > 0 && !string.Equals(action, actionId, StringComparison.OrdinalIgnoreCase))
                previous = action;
        if (!Bindings.TryGetValue(actionId, out var mine)) Bindings[actionId] = mine = new();
        mine.Add(input);
        return previous;
    }

    public void Clear(string actionId) => Bindings.Remove(actionId);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>
    /// Loads saved bindings. New actions added in later versions get their default key unless that
    /// key is already used. Falls back to the older keybindings.json (action → key) if present.
    /// </summary>
    public static InputBindingSet Load(string path, string? legacyKeyFile = null)
    {
        InputBindingSet? set = null;
        try
        {
            if (File.Exists(path)) set = JsonSerializer.Deserialize<InputBindingSet>(File.ReadAllText(path), Json);
        }
        catch { /* corrupt file: use defaults */ }

        bool fromSaved = set is not null;
        set ??= new InputBindingSet();
        set.Bindings = new(set.Bindings ?? new(), StringComparer.OrdinalIgnoreCase);

        if (!fromSaved && legacyKeyFile is not null && File.Exists(legacyKeyFile))
        {
            try
            {
                var legacy = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(legacyKeyFile));
                foreach (var (action, vk) in legacy ?? new())
                    if (vk != 0 && InputActions.Find(action) is not null) set.Bind(action, new InputBinding(InputSource.Keyboard, vk));
            }
            catch { }
        }

        // Fill in defaults for actions that have never been configured.
        foreach (var a in InputActions.All.Where(a => a.DefaultKey != 0 && !set.Bindings.ContainsKey(a.Id)))
        {
            var key = new InputBinding(InputSource.Keyboard, a.DefaultKey);
            if (set.ActionFor(key) is null) set.Bindings[a.Id] = new() { key };
        }
        return set;
    }
}

/// <summary>
/// Turns raw key and controller-button presses into actions. Handles hold actions (push-to-talk),
/// ignores key auto-repeat, and can capture the next input for the binding dialog.
/// </summary>
public sealed class InputRouter
{
    private readonly HashSet<InputBinding> _down = new(new SameInputComparer());
    private readonly HashSet<InputBinding> _captured = new(new SameInputComparer());
    private Action<InputBinding>? _capture;

    public InputBindingSet Bindings { get; set; }

    /// <summary>Push-to-talk works as a toggle instead of hold when false.</summary>
    public bool PttHoldMode { get; set; } = true;
    private bool _pttLatched;

    public event EventHandler<string>? ActionPressed;
    public event EventHandler<string>? ActionReleased;

    public InputRouter(InputBindingSet bindings) => Bindings = bindings;

    /// <summary>The next press (key or button) goes to <paramref name="onInput"/> instead of triggering an action.</summary>
    public void CaptureNext(Action<InputBinding> onInput) => _capture = onInput;
    public void CancelCapture() => _capture = null;
    public bool IsCapturing => _capture is not null;

    public void Press(InputBinding input)
    {
        if (!_down.Add(input)) return; // auto-repeat or a held button
        if (_capture is { } capture)
        {
            _capture = null;
            _captured.Add(input); // its release must not trigger the action it was just bound to
            capture(input);
            return;
        }
        if (Bindings.ActionFor(input) is not { } action) return;
        if (string.Equals(action, InputActions.PushToTalk, StringComparison.OrdinalIgnoreCase) && !PttHoldMode)
        {
            _pttLatched = !_pttLatched;
            if (_pttLatched) ActionPressed?.Invoke(this, action);
            else ActionReleased?.Invoke(this, action);
            return;
        }
        ActionPressed?.Invoke(this, action);
    }

    public void Release(InputBinding input)
    {
        if (!_down.Remove(input)) return;
        if (_captured.Remove(input)) return;
        if (Bindings.ActionFor(input) is not { } action) return;
        if (InputActions.Find(action) is { IsHold: true } && (PttHoldMode || action != InputActions.PushToTalk))
            ActionReleased?.Invoke(this, action);
    }

    private sealed class SameInputComparer : IEqualityComparer<InputBinding>
    {
        public bool Equals(InputBinding? x, InputBinding? y) => x is not null && y is not null && x.SameInput(y);
        public int GetHashCode(InputBinding b) => HashCode.Combine(b.Source, b.Code,
            b.Source == InputSource.Controller ? (b.DeviceId ?? "").ToLowerInvariant() : "");
    }
}

/// <summary>Readable names for Windows virtual-key codes.</summary>
public static class KeyNames
{
    public static string Name(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Numpad {vk - 0x60}",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause", 0x14 => "Caps Lock", 0x1B => "Esc",
        0x20 => "Space", 0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x2D => "Insert", 0x2E => "Delete",
        0x6A => "Numpad *", 0x6B => "Numpad +", 0x6D => "Numpad -", 0x6E => "Numpad .", 0x6F => "Numpad /",
        0x90 => "Num Lock", 0x91 => "Scroll Lock",
        0xA0 => "Left Shift", 0xA1 => "Right Shift", 0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl", 0xA4 => "Left Alt", 0xA5 => "Right Alt",
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
        0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        _ => $"Key 0x{vk:X2}"
    };
}
