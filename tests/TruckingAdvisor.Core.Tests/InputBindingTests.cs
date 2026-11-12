using TruckingAdvisor.Shared.Input;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public class InputBindingTests
{
    private static InputBinding Key(int vk) => new(InputSource.Keyboard, vk);
    private static InputBinding Wheel(int button, string device = "wheel-1") => new(InputSource.Controller, button, device, "G29 Racing Wheel");

    [Fact]
    public void Defaults_CoverTheClassicKeys()
    {
        var set = InputBindingSet.Defaults();
        Assert.Equal("pushToTalk", set.ActionFor(Key(0xA4)));
        Assert.Equal("cycleCbChannel", set.ActionFor(Key(0xDD)));
        Assert.Equal("previousCbChannel", set.ActionFor(Key(0xDB)));
        Assert.Equal("toggleOverlay", set.ActionFor(Key(0x77)));
        Assert.Null(InputActions.Find("channel19")); // channels are scrolled, not bound one by one
    }

    [Fact]
    public void WheelButton_TriggersChannelUpAndDown()
    {
        var set = InputBindingSet.Defaults();
        set.Bind("cycleCbChannel", Wheel(4));
        set.Bind("previousCbChannel", Wheel(5));
        var router = new InputRouter(set);
        var pressed = new List<string>();
        router.ActionPressed += (_, a) => pressed.Add(a);

        router.Press(Wheel(4)); router.Release(Wheel(4));
        router.Press(Wheel(5)); router.Release(Wheel(5));
        router.Press(Key(0xDD)); router.Release(Key(0xDD)); // keyboard still works alongside

        Assert.Equal(new[] { "cycleCbChannel", "previousCbChannel", "cycleCbChannel" }, pressed);
    }

    [Fact]
    public void SameButtonNumber_OnDifferentDevices_AreDifferentInputs()
    {
        var set = new InputBindingSet();
        set.Bind("cycleCbChannel", Wheel(0, "wheel-1"));
        set.Bind("previousCbChannel", Wheel(0, "buttonbox-1"));
        Assert.Equal("cycleCbChannel", set.ActionFor(Wheel(0, "wheel-1")));
        Assert.Equal("previousCbChannel", set.ActionFor(Wheel(0, "buttonbox-1")));
    }

    [Fact]
    public void BindingAnInputElsewhere_MovesIt()
    {
        var set = InputBindingSet.Defaults();
        string? from = set.Bind("toggleQuiet", Key(0xDD)); // ] was "next channel"
        Assert.Equal("cycleCbChannel", from);
        Assert.Equal("toggleQuiet", set.ActionFor(Key(0xDD)));
        Assert.Empty(set.For("cycleCbChannel"));
    }

    [Fact]
    public void PushToTalk_HoldsOnAButton_AndIgnoresRepeats()
    {
        var set = InputBindingSet.Defaults();
        set.Bind("pushToTalk", Wheel(2));
        var router = new InputRouter(set);
        var log = new List<string>();
        router.ActionPressed += (_, a) => log.Add("down:" + a);
        router.ActionReleased += (_, a) => log.Add("up:" + a);

        router.Press(Wheel(2));
        router.Press(Wheel(2)); // still held
        router.Release(Wheel(2));

        Assert.Equal(new[] { "down:pushToTalk", "up:pushToTalk" }, log);
    }

    [Fact]
    public void Capture_TakesTheNextInput_AndItsReleaseDoesNothing()
    {
        var set = InputBindingSet.Defaults();
        var router = new InputRouter(set);
        var log = new List<string>();
        router.ActionPressed += (_, a) => log.Add("down:" + a);
        router.ActionReleased += (_, a) => log.Add("up:" + a);

        InputBinding? got = null;
        router.CaptureNext(b => { got = b; set.Bind("pushToTalk", b); });
        router.Press(Wheel(7));
        router.Release(Wheel(7));

        Assert.Equal(Wheel(7), got);
        Assert.Empty(log); // binding PTT didn't start a transmission
        router.Press(Wheel(7));
        Assert.Equal(new[] { "down:pushToTalk" }, log);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_AndNewActionsGetDefaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ta-controls-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "controls.json");
        try
        {
            var set = new InputBindingSet();
            set.Bind("cycleCbChannel", Wheel(4));
            set.Save(path);

            var loaded = InputBindingSet.Load(path);
            Assert.Equal("cycleCbChannel", loaded.ActionFor(Wheel(4)));
            Assert.Equal("G29 Racing Wheel", loaded.For("cycleCbChannel")[0].DeviceName);
            // Actions never configured get their default key.
            Assert.Equal("toggleOverlay", loaded.ActionFor(Key(0x77)));
            // An action the player configured keeps only what they chose (no default ] added back).
            Assert.Null(loaded.ActionFor(Key(0xDD)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Load_DropsFunctionsThatNoLongerExist()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ta-controls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "controls.json");
            File.WriteAllText(path, "{\"Bindings\":{\"channel19\":[{\"Source\":\"Controller\",\"Code\":3,\"DeviceId\":\"wheel-1\"}]}}");
            var set = InputBindingSet.Load(path);
            Assert.Null(set.ActionFor(Wheel(3)));
            Assert.Equal("cycleCbChannel", set.ActionFor(Key(0xDD)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Load_MigratesOldKeyboardOnlyFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ta-controls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string legacy = Path.Combine(dir, "keybindings.json");
            File.WriteAllText(legacy, "{\"pushToTalk\": 86, \"toggleOverlay\": 119}"); // V
            var set = InputBindingSet.Load(Path.Combine(dir, "controls.json"), legacy);
            Assert.Equal("pushToTalk", set.ActionFor(Key(86)));
            Assert.Null(set.ActionFor(Key(0xA4))); // Left Alt no longer PTT
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData(-1, false, false, false, false)]
    [InlineData(0, true, false, false, false)]
    [InlineData(9000, false, true, false, false)]
    [InlineData(18000, false, false, true, false)]
    [InlineData(27000, false, false, false, true)]
    [InlineData(4500, true, true, false, false)] // diagonal up-right
    public void PovAngles_MapToDpadDirections(int angle, bool up, bool right, bool down, bool left)
    {
        bool[] d = InputBinding.PovDirections(angle);
        Assert.Equal(new[] { up, right, down, left }, d);
    }

    [Fact]
    public void Display_NamesKeysButtonsAndDpad()
    {
        Assert.Equal("Left Alt", Key(0xA4).Display);
        Assert.Equal("G29 Racing Wheel: Button 5", Wheel(4).Display);
        Assert.Equal("G29 Racing Wheel: D-pad Left", Wheel(InputBinding.PovBase + 3).Display);
    }
}
