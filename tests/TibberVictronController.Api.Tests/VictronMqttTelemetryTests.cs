using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Mqtt;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Tests;

public sealed class VictronMqttTelemetryTests
{
    private static readonly DateTimeOffset SampleAt = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static EvChargerSnapshot Wallbox(EvConnectorStatus status, double? watts = null, int ageMinutes = 0, bool connected = true, int? transaction = null) =>
        new() { IsConnected = connected, ConnectorStatus = status, PowerWatts = watts, MeasuredAtUtc = watts is null ? null : SampleAt.AddMinutes(-ageMinutes), ActiveTransactionId = transaction };

    [Fact]
    public void WallboxPowerIsStoredAsZeroWhenKnownIdleAndAsNullOnlyWhenUnknown()
    {
        // Known: a current reading, no car, or a car that the wallbox itself reports as not charging.
        Assert.Equal(7400m, VictronMqttClientService.WallboxWattsForSample(Wallbox(EvConnectorStatus.Charging, 7400, 1, transaction: 5), SampleAt));
        Assert.Equal(0m, VictronMqttClientService.WallboxWattsForSample(Wallbox(EvConnectorStatus.Available), SampleAt));
        Assert.Equal(0m, VictronMqttClientService.WallboxWattsForSample(Wallbox(EvConnectorStatus.SuspendedEv, 0, 30, transaction: 5), SampleAt));
        Assert.Equal(0m, VictronMqttClientService.WallboxWattsForSample(Wallbox(EvConnectorStatus.Finishing), SampleAt));
        Assert.Equal(0m, VictronMqttClientService.WallboxWattsForSample(Wallbox(EvConnectorStatus.Preparing), SampleAt));

        // Unknown: no wallbox connection, a charge without a current reading, an open transaction with an outdated status.
        Assert.Null(VictronMqttClientService.WallboxWattsForSample(null, SampleAt));
        Assert.Null(VictronMqttClientService.WallboxWattsForSample(Wallbox(EvConnectorStatus.Available, connected: false), SampleAt));
        Assert.Null(VictronMqttClientService.WallboxWattsForSample(Wallbox(EvConnectorStatus.Charging, 7400, 5, transaction: 5), SampleAt));
        Assert.Null(VictronMqttClientService.WallboxWattsForSample(Wallbox(EvConnectorStatus.Available, transaction: 5), SampleAt));
    }

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
