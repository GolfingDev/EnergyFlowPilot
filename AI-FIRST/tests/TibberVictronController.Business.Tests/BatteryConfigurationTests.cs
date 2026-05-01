using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class BatteryConfigurationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsInvalidTotalCapacity(decimal invalidTotalCapacityKwh)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new BatteryConfiguration(invalidTotalCapacityKwh));

        Assert.Contains("Die Batteriekapazitaet muss groesser als 0 kWh sein.", exception.Message);
    }

    [Fact]
    public void ConstructorRejectsInvalidPowerAndEfficiencyValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BatteryConfiguration(10m, minimumStateOfChargePercent: 10m, maximumChargePowerWatts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BatteryConfiguration(10m, minimumStateOfChargePercent: 10m, maximumChargePowerWatts: 3000, maximumDischargePowerWatts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BatteryConfiguration(10m, minimumStateOfChargePercent: 10m, maximumChargePowerWatts: 3000, maximumDischargePowerWatts: 3000, roundTripEfficiencyPercent: 0m));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BatteryConfiguration(10m, minimumStateOfChargePercent: 20m, targetEndStateOfChargePercent: 10m));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(101)]
    public void ConstructorRejectsInvalidMinimumStateOfCharge(decimal invalidMinimumStateOfChargePercent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BatteryConfiguration(10m, invalidMinimumStateOfChargePercent));
    }

    [Fact]
    public void ConstructorAcceptsConfiguredBatteryLimits()
    {
        var configuration = new BatteryConfiguration(
            10.5m,
            minimumStateOfChargePercent: 12m,
            maximumChargePowerWatts: 2500,
            maximumDischargePowerWatts: 3200,
            roundTripEfficiencyPercent: 92m,
            targetEndStateOfChargePercent: 30m);

        Assert.Equal(10.5m, configuration.TotalCapacityKwh);
        Assert.Equal(12m, configuration.MinimumStateOfChargePercent);
        Assert.Equal(2500, configuration.MaximumChargePowerWatts);
        Assert.Equal(3200, configuration.MaximumDischargePowerWatts);
        Assert.Equal(92m, configuration.RoundTripEfficiencyPercent);
        Assert.Equal(30m, configuration.TargetEndStateOfChargePercent);
    }
}
