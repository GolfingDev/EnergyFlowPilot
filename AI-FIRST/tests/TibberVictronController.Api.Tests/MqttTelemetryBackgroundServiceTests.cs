using TibberVictronController.Api.Configuration;
using TibberVictronController.Dal.Mqtt;

namespace TibberVictronController.Api.Tests;

public sealed class MqttTelemetryBackgroundServiceTests
{
    [Fact]
    public void ApplyTelemetryValueUpdatesGridAndHouseConsumptionWhenBothUseSameTopic()
    {
        var measuredAtUtc = new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero);
        var snapshotStore = new MqttTelemetrySnapshotStore();
        var topics = new MqttTelemetryTopics
        {
            GridPowerTopic = "N/c0619ab93165/grid/30/Ac/Power",
            BatterySocTopic = "battery/soc",
            BatteryPowerTopic = "battery/power",
            HouseConsumptionTopic = "N/c0619ab93165/grid/30/Ac/Power",
            ChargeDischargeSetpointTopic = "write/setpoint"
        };

        var shouldPersistConsumptionSample = MqttTelemetryBackgroundService.ApplyTelemetryValue(
            "N/c0619ab93165/grid/30/Ac/Power",
            1425.6m,
            measuredAtUtc,
            topics,
            snapshotStore);

        var snapshot = snapshotStore.GetSnapshot();

        Assert.True(shouldPersistConsumptionSample);
        Assert.Equal(1425.6m, snapshot.GridPowerWatts);
        Assert.Equal(1425.6m, snapshot.HouseConsumptionWatts);
        Assert.Equal(measuredAtUtc, snapshot.GridPowerMeasuredAtUtc);
        Assert.Equal(measuredAtUtc, snapshot.HouseConsumptionMeasuredAtUtc);
    }

    [Fact]
    public async Task MqttProvidersReturnPlausibleValuesFromFreshSnapshot()
    {
        var measuredAtUtc = new DateTimeOffset(2026, 5, 3, 12, 5, 0, TimeSpan.Zero);
        var snapshotStore = new MqttTelemetrySnapshotStore();

        snapshotStore.UpdateBatterySoc(37.4m, measuredAtUtc);
        snapshotStore.UpdateGridPower(1980.2m, measuredAtUtc);
        snapshotStore.UpdateHouseConsumption(1980.2m, measuredAtUtc);

        var batteryStateProvider = new MqttBatteryStateProvider(snapshotStore);
        var siteTelemetryProvider = new MqttCurrentSiteTelemetryProvider(snapshotStore);

        var batteryState = await batteryStateProvider.GetCurrentBatteryStateAsync();
        var telemetry = await siteTelemetryProvider.GetCurrentSiteTelemetryAsync();

        Assert.InRange(batteryState.StateOfChargePercent, 0m, 100m);
        Assert.InRange(telemetry.CurrentGridImportWatts, 0, 50000);
        Assert.Equal(0, telemetry.CurrentPvProductionWatts);
        Assert.Equal(measuredAtUtc, batteryState.MeasuredAtUtc);
        Assert.Equal(measuredAtUtc, telemetry.MeasuredAtUtc);
    }
}
