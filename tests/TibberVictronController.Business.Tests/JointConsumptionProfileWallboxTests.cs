using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class JointConsumptionProfileWallboxTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WallboxPowerIsNotLearnedAsHouseDemand()
    {
        // 11.8 kW from the grid, both batteries idle, no PV: 11 kW of it is the car.
        var sample = new LiveConsumptionSample(11800, At, 11800, 0, 50, 0)
        {
            SupplementaryBatteryPowerWatts = 0,
            SupplementaryPvProductionWatts = 0,
            EvChargingWatts = 11000
        };

        Assert.Equal(800, JointConsumptionProfile.GrossWatts(sample));
    }

    [Fact]
    public void SamplesWithoutWallboxDataStayUnchanged()
    {
        var sample = new LiveConsumptionSample(900, At, 900, 0, 50, 0) { SupplementaryBatteryPowerWatts = 0, SupplementaryPvProductionWatts = 0 };

        Assert.Equal(900, JointConsumptionProfile.GrossWatts(sample));
    }
}
