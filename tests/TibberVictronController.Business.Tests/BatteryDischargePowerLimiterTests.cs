using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class BatteryDischargePowerLimiterTests
{
    [Fact]
    public void CalculateTargetPowerUsesCurrentGridImportToAvoidGridFeedIn()
    {
        var limiter = new BatteryDischargePowerLimiter();
        var batteryConfiguration = new BatteryConfiguration(12m, maximumDischargePowerWatts: 3000);

        var targetPowerWatts = limiter.CalculateTargetPowerWatts(currentGridImportWatts: 1200, batteryConfiguration);

        Assert.Equal(1200, targetPowerWatts);
    }

    [Fact]
    public void CalculateTargetPowerUsesMaximumDischargePowerWhenGridImportIsHigher()
    {
        var limiter = new BatteryDischargePowerLimiter();
        var batteryConfiguration = new BatteryConfiguration(12m, maximumDischargePowerWatts: 3000);

        var targetPowerWatts = limiter.CalculateTargetPowerWatts(currentGridImportWatts: 4200, batteryConfiguration);

        Assert.Equal(3000, targetPowerWatts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public void CalculateTargetPowerReturnsZeroWhenThereIsNoGridImport(int currentGridImportWatts)
    {
        var limiter = new BatteryDischargePowerLimiter();
        var batteryConfiguration = new BatteryConfiguration(12m, maximumDischargePowerWatts: 3000);

        var targetPowerWatts = limiter.CalculateTargetPowerWatts(currentGridImportWatts, batteryConfiguration);

        Assert.Equal(0, targetPowerWatts);
    }
}
