using TruckingAdvisor.AI;
using TruckingAdvisor.Presentation;
using TruckingAdvisor.Shared.Input;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public class GuideTests
{
    [Fact]
    public void EveryVoiceCommandExample_IsUnderstoodByTheApp()
    {
        var expected = new Dictionary<string, VoiceCommandKind>
        {
            ["Change channel (1-40)"] = VoiceCommandKind.SwitchChannel,
            ["Call dispatch"] = VoiceCommandKind.CallDispatch,
            ["Fuel and range"] = VoiceCommandKind.FuelReport,
            ["Distance and ETA"] = VoiceCommandKind.DistanceReport,
            ["Repeat the last advice"] = VoiceCommandKind.RepeatLast,
        };
        foreach (var (what, examples) in Guide.VoiceCommands)
            foreach (var say in examples)
            {
                var cmd = VoiceCommandParser.TryParse(say);
                Assert.NotNull(cmd);
                Assert.NotEqual(VoiceCommandKind.Unknown, cmd!.Kind);
                if (expected.TryGetValue(what, out var kind)) Assert.Equal(kind, cmd.Kind);
            }
        Assert.Equal(VoiceCommandKind.VolumeUp, VoiceCommandParser.TryParse("TruckingAdvisor, turn it up")!.Kind);
        Assert.Equal(VoiceCommandKind.Unmute, VoiceCommandParser.TryParse("TruckingAdvisor, unmute")!.Kind);
        Assert.Equal(19, VoiceCommandParser.TryParse("Advisor, channel one-nine")!.Channel);
    }

    [Fact]
    public void HandleExamples_AreRecognised()
    {
        Assert.Equal("Night Owl", HandleDetector.TryDetect("Call me Night Owl"));
        Assert.Equal("Night Owl", HandleDetector.TryDetect("My handle's Night Owl"));
    }

    [Fact]
    public void ControlsPage_ShowsThePlayersOwnBindings_AndEveryFunction()
    {
        var set = InputBindingSet.Defaults();
        set.Bind("cycleCbChannel", new InputBinding(InputSource.Controller, 4, "wheel-1", "G29 Racing Wheel"));
        var controls = Guide.Build(set).Single(t => t.Title == "Controls & key bindings");
        Assert.Contains(controls.Lines, l => l.StartsWith("Next CB channel |") && l.Contains("G29 Racing Wheel: Button 5") && l.Contains("]"));
        Assert.Contains(controls.Lines, l => l.StartsWith("Quiet 30 min on/off | not bound"));
        foreach (var a in InputActions.All) Assert.Contains(controls.Lines, l => l.StartsWith(a.Label + " |"));
    }

    [Fact]
    public void Search_FindsTopics()
    {
        var topics = Guide.Build(InputBindingSet.Defaults());
        Assert.Contains(topics.Where(t => t.Matches("dispatch")), t => t.Title == "Dispatch");
        Assert.Contains(topics.Where(t => t.Matches("wheel")), t => t.Title == "Controls & key bindings");
        Assert.Empty(topics.Where(t => t.Matches("zzzz-nothing")));
        Assert.Equal(topics.Count, topics.Count(t => t.Matches("")));
    }
}
