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
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BatteryConfiguration(new BatteryConfigurationValues
            {
                TotalCapacityKwh = 10m,
                MinimumStateOfChargePercent = 20m,
                PlanningMinimumStateOfChargePercent = 10m
            }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BatteryConfiguration(new BatteryConfigurationValues
            {
                TotalCapacityKwh = 10m,
                TargetEndStateOfChargePercent = 25m,
                PlanningMaximumStateOfChargePercent = 20m
            }));
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
        var configuration = new BatteryConfiguration(new BatteryConfigurationValues
        {
            TotalCapacityKwh = 10.5m,
            MinimumStateOfChargePercent = 12m,
            MaximumChargePowerWatts = 2500,
            MaximumDischargePowerWatts = 3200,
            RoundTripEfficiencyPercent = 92m,
            TargetEndStateOfChargePercent = 30m,
            PlanningMinimumStateOfChargePercent = 18m,
            PlanningMaximumStateOfChargePercent = 88m
        });

        Assert.Equal(10.5m, configuration.TotalCapacityKwh);
        Assert.Equal(12m, configuration.MinimumStateOfChargePercent);
        Assert.Equal(2500, configuration.MaximumChargePowerWatts);
        Assert.Equal(3200, configuration.MaximumDischargePowerWatts);
        Assert.Equal(92m, configuration.RoundTripEfficiencyPercent);
        Assert.Equal(30m, configuration.TargetEndStateOfChargePercent);
        Assert.Equal(18m, configuration.PlanningMinimumStateOfChargePercent);
        Assert.Equal(88m, configuration.PlanningMaximumStateOfChargePercent);
    }

    [Fact]
    public void ConstructorUsesMinimumStateOfChargeAsPlanningFallback()
    {
        var configuration = new BatteryConfiguration(new BatteryConfigurationValues
        {
            TotalCapacityKwh = 10m,
            MinimumStateOfChargePercent = 12m
        });

        Assert.Equal(12m, configuration.PlanningMinimumStateOfChargePercent);
        Assert.Equal(100m, configuration.PlanningMaximumStateOfChargePercent);
    }
}
