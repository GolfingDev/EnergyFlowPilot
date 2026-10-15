using TibberVictronController.Dal.Battery;
using TibberVictronController.Dal.HagerEnergy;

namespace TibberVictronController.Api.Tests;

public sealed class SupplementaryBatteryChargeTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 14, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2800, 0, 2800)]
    [InlineData(2000, 2500, 0)]
    [InlineData(2000, 500, 1500)]
    [InlineData(-300, 0, 0)]
    public void CalculateSupplementaryBatteryChargeFromSiteWattsCountsOnlyChargingBeyondOwnPv(
        int batteryPowerWatts,
        int pvProductionWatts,
        int expectedChargeFromSiteWatts)
    {
        var chargeFromSiteWatts = SelectedCurrentSiteTelemetryProvider.CalculateSupplementaryBatteryChargeFromSiteWatts(
            CreateSnapshot(batteryPowerWatts, pvProductionWatts, NowUtc),
            NowUtc);

        Assert.Equal(expectedChargeFromSiteWatts, chargeFromSiteWatts);
    }

    [Fact]
    public void CalculateSupplementaryBatteryChargeFromSiteWattsIgnoresStaleSnapshot()
    {
        var chargeFromSiteWatts = SelectedCurrentSiteTelemetryProvider.CalculateSupplementaryBatteryChargeFromSiteWatts(
            CreateSnapshot(2800, 0, NowUtc.AddMinutes(-10)),
            NowUtc);

        Assert.Equal(0, chargeFromSiteWatts);
    }

    private static HagerEnergyTelemetrySnapshot CreateSnapshot(int batteryPowerWatts, int pvProductionWatts, DateTimeOffset polledAtUtc)
    {
        return new HagerEnergyTelemetrySnapshot(
            GridImportWatts: 0,
            PvProductionWatts: pvProductionWatts,
            BatterySocPercent: 60,
            BatteryPowerWatts: batteryPowerWatts,
            MeasuredAtUtc: polledAtUtc,
            LastSuccessfulPollAtUtc: polledAtUtc);
    }
}
