using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Dal.Tests;

public sealed class VictronCurrentSiteTelemetryProviderTests
{
    [Fact]
    public async Task GetCurrentSiteTelemetryAsyncMapsGridImportAndNegativeHouseConsumptionToPv()
    {
        var snapshotStore = new VictronTelemetrySnapshotStore();
        var gridMeasuredAtUtc = new DateTimeOffset(2026, 5, 2, 10, 15, 0, TimeSpan.Zero);
        var houseMeasuredAtUtc = new DateTimeOffset(2026, 5, 2, 10, 16, 0, TimeSpan.Zero);
        snapshotStore.UpdateGridPower(1234m, gridMeasuredAtUtc);
        snapshotStore.UpdateHouseConsumption(-678m, houseMeasuredAtUtc);
        var provider = new VictronCurrentSiteTelemetryProvider(snapshotStore);

        var result = await provider.GetCurrentSiteTelemetryAsync();

        Assert.Equal(1234, result.CurrentGridImportWatts);
        Assert.Equal(678, result.CurrentPvProductionWatts);
        Assert.Equal(gridMeasuredAtUtc, result.MeasuredAtUtc);
    }
}
