using TibberVictronController.Api.Configuration;
using TibberVictronController.Dal.Mqtt;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Tests;

public sealed class MqttTelemetryBackgroundServiceTests
{
    [Fact]
    public void InitialKeepAlivePayloadRequestsFullPublish()
    {
        var payload = VictronMqttClientService.CreateKeepAlivePayload(suppressRepublish: false);

        Assert.Equal(string.Empty, payload);
    }

    [Fact]
    public void FollowUpKeepAlivePayloadSuppressesFullRepublish()
    {
        var payload = VictronMqttClientService.CreateKeepAlivePayload(suppressRepublish: true);

        Assert.Equal("""{"keepalive-options":["suppress-republish"]}""", payload);
    }

    [Fact]
    public void DryRunChangeDoesNotRequireMqttReconnect()
    {
        var connectedSettings = CreateVictronMqttSettings(dryRun: false);
        var changedSettings = CreateVictronMqttSettings(dryRun: true);

        Assert.False(VictronMqttClientService.RequiresReconnect(connectedSettings, changedSettings));
    }

    [Fact]
    public void HostChangeRequiresMqttReconnect()
    {
        var connectedSettings = CreateVictronMqttSettings(host: "192.168.69.92");
        var changedSettings = CreateVictronMqttSettings(host: "192.168.69.93");

        Assert.True(VictronMqttClientService.RequiresReconnect(connectedSettings, changedSettings));
    }

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

    [Fact]
    public async Task MqttCurrentSiteTelemetryProviderUsesGridPowerWhenHouseConsumptionIsZero()
    {
        var measuredAtUtc = new DateTimeOffset(2026, 5, 3, 12, 10, 0, TimeSpan.Zero);
        var snapshotStore = new MqttTelemetrySnapshotStore();

        snapshotStore.UpdateGridPower(-1793m, measuredAtUtc);
        snapshotStore.UpdateHouseConsumption(0m, measuredAtUtc);

        var siteTelemetryProvider = new MqttCurrentSiteTelemetryProvider(snapshotStore);

        var telemetry = await siteTelemetryProvider.GetCurrentSiteTelemetryAsync();

        Assert.Equal(-1793, telemetry.CurrentGridImportWatts);
        Assert.Equal(1793, telemetry.CurrentPvProductionWatts);
        Assert.Equal(measuredAtUtc, telemetry.MeasuredAtUtc);
    }

    [Fact]
    public async Task MqttBatteryStateProviderKeepsRecentSocWhenReconnectPreservesSnapshot()
    {
        var measuredAtUtc = new DateTimeOffset(2026, 5, 3, 12, 15, 0, TimeSpan.Zero);
        var snapshotStore = new MqttTelemetrySnapshotStore();

        snapshotStore.UpdateBatterySoc(82.3m, measuredAtUtc);
        snapshotStore.Clear(preserveLatestValues: true);

        var batteryStateProvider = new MqttBatteryStateProvider(snapshotStore);
        var batteryState = await batteryStateProvider.GetCurrentBatteryStateAsync();

        Assert.Equal(82.3m, batteryState.StateOfChargePercent);
        Assert.Equal(measuredAtUtc, batteryState.MeasuredAtUtc);
    }

    [Fact]
    public void MqttTelemetrySnapshotStoreHardClearRemovesBatterySoc()
    {
        var measuredAtUtc = new DateTimeOffset(2026, 5, 3, 12, 20, 0, TimeSpan.Zero);
        var snapshotStore = new MqttTelemetrySnapshotStore();

        snapshotStore.UpdateBatterySoc(82.3m, measuredAtUtc);
        snapshotStore.Clear();

        var snapshot = snapshotStore.GetSnapshot();

        Assert.Null(snapshot.BatterySocPercent);
        Assert.Null(snapshot.BatterySocMeasuredAtUtc);
    }

    private static VictronMqttSettings CreateVictronMqttSettings(
        string host = "192.168.69.92",
        bool dryRun = false)
    {
        return new VictronMqttSettings
        {
            Host = host,
            Port = 1883,
            PortalId = "c0619ab93165",
            KeepAliveSeconds = 15,
            StaleAfterSeconds = 30,
            DryRun = dryRun,
            ControlMode = VictronControlMode.ExternalEss,
            GridPowerTopicTemplate = "N/{portalId}/grid/30/Ac/Power",
            BatterySocTopicTemplate = "N/{portalId}/battery/512/Soc",
            BatteryPowerTopicTemplate = "N/{portalId}/battery/512/Dc/0/Power",
            HouseConsumptionTopicTemplate = "N/{portalId}/system/0/Ac/Consumption/L1/Power",
            ChargeDischargeSetpointTopic = "W/{portalId}/settings/0/Settings/CGwacs/AcPowerSetPoint",
            Hub4ModeTopic = "W/{portalId}/settings/0/Settings/CGwacs/Hub4Mode",
            SwitchEssModeViaMqtt = true,
            ExternalEssPhaseCount = 3,
            ExternalEssL1AcPowerSetpointTopic = "W/{portalId}/vebus/276/Hub4/L1/AcPowerSetpoint",
            ExternalEssL2AcPowerSetpointTopic = "W/{portalId}/vebus/276/Hub4/L2/AcPowerSetpoint",
            ExternalEssL3AcPowerSetpointTopic = "W/{portalId}/vebus/276/Hub4/L3/AcPowerSetpoint",
            DisableChargeTopic = "W/{portalId}/vebus/276/Hub4/DisableCharge",
            DisableFeedInTopic = "W/{portalId}/vebus/276/Hub4/DisableFeedIn",
            BatteryIdleThresholdWatts = 100
        };
    }
}
