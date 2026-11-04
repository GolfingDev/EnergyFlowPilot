using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class SupplementaryBatteryReadingTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);

    [Fact]
    public void NewestReadingWinsSoAStoppedChargeShowsAtOnce()
    {
        var cloud = new SupplementaryBatteryReading(NowUtc.AddSeconds(-50), 61m, 2400m);
        var local = new SupplementaryBatteryReading(NowUtc.AddSeconds(-1), 62m, 0m);

        Assert.Same(local, SupplementaryBatteryReading.Newest(NowUtc, MaximumAge, cloud, local));
        Assert.Same(local, SupplementaryBatteryReading.Newest(NowUtc, MaximumAge, local, cloud));
    }

    [Fact]
    public void MissingOrTooOldReadingsAreSkipped()
    {
        var oldLocal = new SupplementaryBatteryReading(NowUtc.AddMinutes(-6), 50m, -800m);
        var cloud = new SupplementaryBatteryReading(NowUtc.AddSeconds(-40), 55m, -1200m);

        Assert.Same(cloud, SupplementaryBatteryReading.Newest(NowUtc, MaximumAge, null, oldLocal, cloud));
        Assert.Null(SupplementaryBatteryReading.Newest(NowUtc, MaximumAge, null, oldLocal));
    }
}
