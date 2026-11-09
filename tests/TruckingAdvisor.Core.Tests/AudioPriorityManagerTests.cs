using TruckingAdvisor.Core.Audio;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public sealed class AudioPriorityManagerTests
{
    /// <summary>Fake speaker: each item "plays" until released or cancelled.</summary>
    private sealed class FakeSink : IAudioSink
    {
        public readonly List<string> Started = new();
        public readonly List<string> Cancelled = new();
        public int Concurrent, MaxConcurrent;
        public TimeSpan Duration = TimeSpan.FromMilliseconds(150);

        public async Task PlayAsync(AudioItem item, float volume, CancellationToken ct)
        {
            lock (Started) { Started.Add(item.Text); MaxConcurrent = Math.Max(MaxConcurrent, ++Concurrent); }
            try { await Task.Delay(Duration, ct); }
            catch (OperationCanceledException) { lock (Started) Cancelled.Add(item.Text); throw; }
            finally { lock (Started) Concurrent--; }
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task CriticalInterruptsChatter_AndNothingOverlaps()
    {
        var sink = new FakeSink { Duration = TimeSpan.FromSeconds(5) };
        using var mgr = new AudioPriorityManager(sink);
        mgr.EnqueueAnnouncement("chatter", AudioPriority.Low, "cb");
        await WaitUntil(() => sink.Started.Contains("chatter"));

        sink.Duration = TimeSpan.FromMilliseconds(50);
        mgr.EnqueueAnnouncement("low air!", AudioPriority.Critical);
        await WaitUntil(() => sink.Started.Contains("low air!"));
        Assert.Contains("chatter", sink.Cancelled);
        Assert.Equal(1, sink.MaxConcurrent);
    }

    [Fact]
    public async Task PlaysHighestPriorityFirst()
    {
        var sink = new FakeSink { Duration = TimeSpan.FromMilliseconds(300) };
        using var mgr = new AudioPriorityManager(sink);
        mgr.EnqueueAnnouncement("first", AudioPriority.Normal);
        await WaitUntil(() => sink.Started.Count == 1);
        mgr.EnqueueAnnouncement("normal", AudioPriority.Normal);
        mgr.EnqueueAnnouncement("high", AudioPriority.High);
        await WaitUntil(() => sink.Started.Count == 3);
        Assert.Equal(new[] { "first", "high", "normal" }, sink.Started);
    }

    [Fact]
    public async Task LowDroppedAndNormalHeldWhileRadioBusy()
    {
        var sink = new FakeSink { Duration = TimeSpan.FromMilliseconds(10) };
        using var mgr = new AudioPriorityManager(sink) { IsRadioChannelBusy = true };
        mgr.EnqueueAnnouncement("low", AudioPriority.Low);
        mgr.EnqueueAnnouncement("normal", AudioPriority.Normal);
        await Task.Delay(400);
        Assert.Empty(sink.Started);

        mgr.IsRadioChannelBusy = false;
        await WaitUntil(() => sink.Started.Count == 1);
        Assert.Equal("normal", sink.Started[0]);
    }

    [Fact]
    public async Task EnqueueAndWait_CompletesWhenPlaybackEnds_OrIsDropped()
    {
        var sink = new FakeSink { Duration = TimeSpan.FromMilliseconds(200) };
        using var mgr = new AudioPriorityManager(sink);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await mgr.EnqueueAndWaitAsync(new AudioItem("hello", AudioPriority.Normal)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sw.ElapsedMilliseconds >= 150, $"returned after {sw.ElapsedMilliseconds} ms, before playback ended");

        mgr.IsRadioChannelBusy = true;
        await mgr.EnqueueAndWaitAsync(new AudioItem("dropped", AudioPriority.Low)).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("dropped", sink.Started);
    }

    [Fact]
    public async Task StopAllStopsPlaybackAndClearsQueue()
    {
        var sink = new FakeSink { Duration = TimeSpan.FromSeconds(5) };
        using var mgr = new AudioPriorityManager(sink);
        mgr.EnqueueAnnouncement("a", AudioPriority.Normal);
        await WaitUntil(() => sink.Started.Count == 1);
        mgr.EnqueueAnnouncement("b", AudioPriority.Normal);
        mgr.StopAll();
        await WaitUntil(() => sink.Cancelled.Contains("a"));
        await Task.Delay(300);
        Assert.DoesNotContain("b", sink.Started);
    }
}
