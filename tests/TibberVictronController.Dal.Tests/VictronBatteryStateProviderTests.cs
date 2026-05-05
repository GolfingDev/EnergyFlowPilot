using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Dal.Tests;

public sealed class VictronBatteryStateProviderTests
{
    [Fact]
    public async Task GetCurrentBatteryStateAsyncReturnsLiveSocFromSnapshot()
    {
        var snapshotStore = new VictronTelemetrySnapshotStore();
        var measuredAtUtc = new DateTimeOffset(2026, 5, 2, 10, 15, 0, TimeSpan.Zero);
        snapshotStore.UpdateBatterySoc(47.5m, measuredAtUtc);
        var provider = new VictronBatteryStateProvider(snapshotStore);

        var result = await provider.GetCurrentBatteryStateAsync();

        Assert.Equal(47.5m, result.StateOfChargePercent);
        Assert.Equal(measuredAtUtc, result.MeasuredAtUtc);
    }
}
