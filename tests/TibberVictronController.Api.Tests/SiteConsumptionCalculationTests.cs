using TibberVictronController.Api.Configuration;
using TibberVictronController.Dal.HagerEnergy;
using TibberVictronController.Dal.Mqtt;

namespace TibberVictronController.Api.Tests;

public sealed class SiteConsumptionCalculationTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryCalculateSiteConsumptionWattsRemovesVictronBatteryAndAddsSupplementaryPv()
    {
        var snapshot = new MqttTelemetrySnapshot
        {
            GridPowerWatts = -500m,
            BatteryPowerWatts = 1500m
        };

        var consumptionWatts = VictronMqttClientService.TryCalculateSiteConsumptionWatts(
            snapshot,
            CreateSupplementaryPvSnapshot(pvProductionWatts: 3000, lastSuccessfulPollAtUtc: NowUtc),
            NowUtc);

        Assert.Equal(1000m, consumptionWatts);
    }

    [Fact]
    public void TryCalculateSiteConsumptionWattsCountsLoadCoveredByVictronBattery()
    {
        var snapshot = new MqttTelemetrySnapshot
        {
            GridPowerWatts = 10m,
            BatteryPowerWatts = -4900m
        };

        var consumptionWatts = VictronMqttClientService.TryCalculateSiteConsumptionWatts(
            snapshot,
            CreateSupplementaryPvSnapshot(pvProductionWatts: 0, lastSuccessfulPollAtUtc: NowUtc),
            NowUtc);

        Assert.Equal(4910m, consumptionWatts);
    }

    [Fact]
    public void TryCalculateSiteConsumptionWattsFallsBackWhenSupplementaryPvIsStale()
    {
        var snapshot = new MqttTelemetrySnapshot
        {
            GridPowerWatts = 10m,
            BatteryPowerWatts = -4900m
        };

        var consumptionWatts = VictronMqttClientService.TryCalculateSiteConsumptionWatts(
            snapshot,
            CreateSupplementaryPvSnapshot(pvProductionWatts: 0, lastSuccessfulPollAtUtc: NowUtc.AddMinutes(-10)),
            NowUtc);

        Assert.Null(consumptionWatts);
    }

    private static HagerEnergyTelemetrySnapshot CreateSupplementaryPvSnapshot(int pvProductionWatts, DateTimeOffset lastSuccessfulPollAtUtc)
    {
        return new HagerEnergyTelemetrySnapshot(
            GridImportWatts: 0,
            PvProductionWatts: pvProductionWatts,
            BatterySocPercent: 50,
            BatteryPowerWatts: null,
            MeasuredAtUtc: lastSuccessfulPollAtUtc,
            LastSuccessfulPollAtUtc: lastSuccessfulPollAtUtc);
    }
}
