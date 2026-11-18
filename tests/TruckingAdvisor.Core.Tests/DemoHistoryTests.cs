using TruckingAdvisor.Presentation;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public sealed class DemoHistoryTests
{
    [Fact]
    public void DemoTrips_AreAllInThePast_OldestFirst()
    {
        var now = new DateTimeOffset(2026, 11, 18, 18, 0, 0, TimeSpan.Zero);
        var trips = DemoHistory.BuildTrips(now);
        Assert.NotEmpty(trips);
        Assert.All(trips, t => Assert.True(t.CompletedAt <= now, $"{t.Cargo} finishes at {t.CompletedAt}, after now"));
        Assert.Equal(trips.OrderBy(t => t.StartedAt).Select(t => t.StartedAt), trips.Select(t => t.StartedAt));
        Assert.True(now - trips[^1].CompletedAt!.Value < TimeSpan.FromHours(3)); // the newest is recent
    }
}
