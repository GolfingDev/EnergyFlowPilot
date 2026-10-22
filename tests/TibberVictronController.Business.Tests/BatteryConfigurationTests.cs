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
            () => new BatteryConfiguration(10m, minimumStateOfChargePercent: 20m, targetEndStateOfChargePercent: -1m));
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
                PlanningMinimumStateOfChargePercent = 20m,
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
    public void LegacyEndReserveDoesNotConstrainThePlanningRange()
    {
        foreach (var (minimum, maximum) in new[] { (30m, 95m), (15m, 20m) })
        {
            var configuration = new BatteryConfiguration(new BatteryConfigurationValues
            {
                TotalCapacityKwh = 10m, MinimumStateOfChargePercent = 10m,
                PlanningMinimumStateOfChargePercent = minimum,
                PlanningMaximumStateOfChargePercent = maximum, TargetEndStateOfChargePercent = 25m
            });
            Assert.Equal(minimum, configuration.PlanningMinimumStateOfChargePercent);
            Assert.Equal(maximum, configuration.PlanningMaximumStateOfChargePercent);
            Assert.Equal(25m, configuration.TargetEndStateOfChargePercent);
        }
        Assert.Equal(10m, new BatteryConfiguration(10m, minimumStateOfChargePercent: 20m,
            targetEndStateOfChargePercent: 10m).TargetEndStateOfChargePercent);
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
