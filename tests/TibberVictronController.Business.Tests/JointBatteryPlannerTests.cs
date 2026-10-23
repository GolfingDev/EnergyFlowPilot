using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class JointBatteryPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 17, 0, 0, TimeSpan.Zero);
    private static JointBatteryUnit Unit(string id, decimal soc, decimal reserve = 10, int maxPower = 3000) =>
        new(id, new BatteryConfiguration(new BatteryConfigurationValues { TotalCapacityKwh = 10,
            MinimumStateOfChargePercent = reserve, PlanningMinimumStateOfChargePercent = reserve,
            PlanningMaximumStateOfChargePercent = 95, MaximumChargePowerWatts = maxPower,
            MaximumDischargePowerWatts = maxPower }), new BatteryState(soc, Now), 0);

    [Fact]
    public void FullBatteryDoesNotHideHeadroomOfOtherBattery()
    {
        var targets = JointBatteryAllocator.Allocate([Unit("victron", 100), Unit("e3dc", 20)], 4000, TimeSpan.FromMinutes(15));
        Assert.Equal(0, targets[0].SignedPowerWatts);
        Assert.Equal(3000, targets[1].SignedPowerWatts);
        Assert.InRange(targets[1].SocAfterPercent, 20, 95);
    }

    [Fact]
    public void ReserveIsNotDischargedAndBothTargetsHaveSameDirection()
    {
        var targets = JointBatteryAllocator.Allocate([Unit("victron", 10), Unit("e3dc", 50, 20)], -4000, TimeSpan.FromMinutes(15));
        Assert.Equal(0, targets[0].SignedPowerWatts);
        Assert.InRange(targets[1].SignedPowerWatts, -3000, -1);
        Assert.True(targets[1].SocAfterPercent >= 20);
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(-3000)]
    public void SeparateEnergyAndPowerLimitsRemainValidAcrossManyStates(int requested)
    {
        foreach (var a in new[] { 10m, 11m, 50m, 94m, 100m })
        foreach (var b in new[] { 10m, 25m, 50m, 95m, 100m })
        {
            var units = new[] { Unit("a", a, 10, 700), Unit("b", b, 10, 1700) };
            var targets = JointBatteryAllocator.Allocate(units, requested, TimeSpan.FromMinutes(15));
            Assert.True(Math.Abs(targets.Sum(t => t.SignedPowerWatts)) <= Math.Abs(requested));
            for (var i = 0; i < targets.Count; i++)
            {
                Assert.True(targets[i].SignedPowerWatts == 0 || Math.Sign(targets[i].SignedPowerWatts) == Math.Sign(requested));
                Assert.InRange(targets[i].SocAfterPercent, Math.Min(10, units[i].State.StateOfChargePercent), Math.Max(95, units[i].State.StateOfChargePercent));
                Assert.True(Math.Abs(targets[i].SignedPowerWatts) <= units[i].Configuration.MaximumChargePowerWatts);
            }
        }
    }

    [Fact]
    public void EstimatedNegativePriceDoesNotPermitGridCharging()
    {
        var time = new ForecastTimeSlot(Now, Now.AddMinutes(15));
        var plan = new JointBatteryPlanner().Calculate(new JointBatteryPlanInput {
            NowUtc = Now, Batteries = [Unit("victron", 10), Unit("e3dc", 20)],
            Prices = [new(time, -.1m, "EUR") { IsEstimated = true }],
            Pv = [new(time, 0)], Consumption = [new(time, .25m)], MaximumGridImportWatts = 4000
        });
        Assert.All(plan.Slots[0].Targets, t => Assert.True(t.SignedPowerWatts <= 0));
    }

    [Fact]
    public void TotalChargeRespectsGridBudgetIncludingHouseLoad()
    {
        var time = new ForecastTimeSlot(Now, Now.AddMinutes(15));
        var plan = new JointBatteryPlanner().Calculate(new JointBatteryPlanInput {
            NowUtc = Now, Batteries = [Unit("victron", 10), Unit("e3dc", 20)],
            Prices = [new(time, -.1m, "EUR")], Pv = [new(time, 0)],
            Consumption = [new(time, .5m)], MaximumGridImportWatts = 2500
        });
        Assert.InRange(plan.Slots[0].Targets.Sum(t => t.SignedPowerWatts), 1, 500);
    }

    [Fact]
    public void UnlimitedOptionalBudgetUsesBothDeviceLimitsAtNegativePublishedPrice()
    {
        var time = new ForecastTimeSlot(Now, Now.AddMinutes(15));
        var plan = new JointBatteryPlanner().Calculate(new JointBatteryPlanInput {
            NowUtc = Now, Batteries = [Unit("victron", 10, maxPower: 700), Unit("e3dc", 20, maxPower: 1700)],
            Prices = [new(time, -.2m, "EUR")], Pv = [new(time, 0)], Consumption = [new(time, .25m)], MaximumGridImportWatts = 0
        });
        Assert.Equal(700, plan.Slots[0].Targets[0].SignedPowerWatts);
        Assert.Equal(1700, plan.Slots[0].Targets[1].SignedPowerWatts);
    }

    [Fact]
    public void PlannerKeepsReserveInEachSeparateEnergyState()
    {
        var slots = Enumerable.Range(0, 4).Select(i => new ForecastTimeSlot(Now.AddMinutes(i * 15), Now.AddMinutes((i + 1) * 15))).ToArray();
        var plan = new JointBatteryPlanner().Calculate(new JointBatteryPlanInput {
            NowUtc = Now, Batteries = [Unit("victron", 10), Unit("e3dc", 80)],
            Prices = slots.Select(s => new TibberPriceForecastSlot(s, .5m, "EUR")).ToArray(),
            Pv = slots.Select(s => new PvYieldForecastSlot(s, 0)).ToArray(),
            Consumption = slots.Select(s => new ConsumptionForecastSlot(s, .25m)).ToArray(), MaximumGridImportWatts = 0
        });
        Assert.All(plan.Slots, s => {
            Assert.Equal(0, s.Targets[0].SignedPowerWatts);
            Assert.InRange(s.Targets[1].SocAfterPercent, 10, 80);
            Assert.InRange(s.Targets.Sum(t => t.SignedPowerWatts), -1000, 0);
        });
        Assert.Contains(plan.Slots, s => s.Targets[1].SignedPowerWatts < 0);
    }

    [Fact]
    public void StaleBatteryRejectsPlanInsteadOfAssumingZeroPower()
    {
        var stale = Unit("e3dc", 50) with { State = new(50, Now.AddMinutes(-5)) };
        Assert.Throws<InvalidOperationException>(() => new JointBatteryPlanner().Calculate(new JointBatteryPlanInput {
            NowUtc = Now, Batteries = [Unit("victron", 50), stale], Prices = [], Pv = [], Consumption = [], MaximumGridImportWatts = 4000
        }));
    }

    [Fact]
    public void ZeroEffectiveLimitBlocksOnlyThatBatterysCharge()
    {
        var targets = JointBatteryAllocator.Allocate([Unit("victron", 20), Unit("e3dc", 20) with { AvailableChargeWatts = 0 }],
            2000, TimeSpan.FromMinutes(15));
        Assert.Equal(2000, targets[0].SignedPowerWatts);
        Assert.Equal(0, targets[1].SignedPowerWatts);
    }

    [Theory]
    [InlineData(500, 2000)]
    [InlineData(-500, 3000)]
    public void JointConsumptionRemovesEitherDirectionOfSecondaryBattery(int secondary, int expected)
    {
        var sample = new LiveConsumptionSample(0, Now, 3000, 500, 50, null) {
            SupplementaryBatteryPowerWatts = secondary, SupplementaryPvProductionWatts = 0
        };
        Assert.Equal(expected, JointConsumptionProfile.GrossWatts(sample));
    }
}
