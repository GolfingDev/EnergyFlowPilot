using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class JointBatterySlotCommitmentTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 7, 0, 0, TimeSpan.Zero);

    private static JointBatterySlot Slot(int victron, int e3dc) => new(new ForecastTimeSlot(Start, Start.AddMinutes(15)), 0.18m, "EUR", false,
        victron + e3dc > 0 ? BatteryChargeSource.Grid : null, [new("victron", victron, 10, 20), new("e3dc", e3dc, 11, 12)], "Test", false);

    private static JointBatteryPlan Plan(JointBatterySlot slot) => new(Start, [], [slot], 0, []);

    [Fact]
    public void CommittedDecisionHoldsForItsWholeQuarterHourOnly()
    {
        var committed = Slot(3000, 500);
        Assert.Same(committed, JointBatterySlotCommitment.Active(committed, Start));
        Assert.Same(committed, JointBatterySlotCommitment.Active(committed, Start.AddMinutes(14).AddSeconds(59)));
        Assert.Null(JointBatterySlotCommitment.Active(committed, Start.AddMinutes(15)));
        Assert.Null(JointBatterySlotCommitment.Active(null, Start));
    }

    [Fact]
    public void ChangedPlanForTheSameQuarterHourIsDetectedButSmallNoiseIsNot()
    {
        var committed = Slot(3000, 500);
        // The situation reported on 24.10.2026: a newer plan drops a started charge.
        Assert.True(JointBatterySlotCommitment.PlanDeviates(committed, Plan(Slot(0, 0))));
        Assert.False(JointBatterySlotCommitment.PlanDeviates(committed, Plan(Slot(2950, 520))));
        Assert.False(JointBatterySlotCommitment.PlanDeviates(committed, null));
    }
    [Fact]
    public void VanishedPvSurplusReleasesEconomicallyPlannedSupportWithinQuarterHour()
    {
        var now = Start.AddMinutes(3);
        var committed = Slot(0, 0) with { PricePerKwh = .4827m, ExpectedPvKwh = .5m, ExpectedConsumptionKwh = .4m };
        JointBatteryUnit Unit(string id) => new(id, new BatteryConfiguration(new BatteryConfigurationValues {
            TotalCapacityKwh = 10, MinimumStateOfChargePercent = 10, PlanningMinimumStateOfChargePercent = 10,
            PlanningMaximumStateOfChargePercent = 95, MaximumChargePowerWatts = 3000, MaximumDischargePowerWatts = 3000
        }), new BatteryState(95, now), 0);
        var times = Enumerable.Range(0, 4).Select(i => new ForecastTimeSlot(Start.AddMinutes(i * 15), Start.AddMinutes((i + 1) * 15))).ToArray();
        var pv = times.Select(t => new PvYieldForecastSlot(t, .305m)).ToArray();
        // Screenshot: 4.03 kW house load minus 1.22 kW PV = 2.81 kW actual net demand.
        var consumption = BatteryForecastService.ApplyCurrentNetLoad(
            times.Select(t => new ConsumptionForecastSlot(t, .4m)).ToArray(), pv, Start, 2810);
        var plan = new JointBatteryPlanner().Calculate(new JointBatteryPlanInput {
            NowUtc = now, Batteries = [Unit("victron"), Unit("e3dc")],
            Prices = times.Select((t, i) => new TibberPriceForecastSlot(t, i == 0 ? .4827m : .5149m, "EUR")).ToArray(),
            Pv = pv, Consumption = consumption, MaximumGridImportWatts = 0
        });
        var replacement = JointBatterySlotCommitment.NetDemandReplacement(committed, plan, now);
        Assert.NotNull(replacement);
        Assert.InRange(replacement.Targets.Sum(t => t.SignedPowerWatts), -2810, -2700);
        Assert.Contains("PV reicht aktuell nicht", replacement.Reason);
        Assert.Equal(committed.TimeSlot, replacement.TimeSlot);
    }

    [Fact]
    public void ReplacementRequiresFreshPlanRealDemandAndEconomicDischarge()
    {
        var committed = Slot(0, 0) with { ExpectedPvKwh = .5m, ExpectedConsumptionKwh = .4m };
        var discharge = Slot(-1500, -1200) with { ExpectedPvKwh = .305m, ExpectedConsumptionKwh = 1.0075m };
        var now = Start.AddSeconds(30);
        JointBatterySlot? Replace(JointBatterySlot old, JointBatterySlot next, DateTimeOffset calculated) =>
            JointBatterySlotCommitment.NetDemandReplacement(old, Plan(next) with { CalculatedAtUtc = calculated }, now);
        Assert.NotNull(Replace(committed, discharge, Start));
        Assert.Null(Replace(committed with { ChargeSource = BatteryChargeSource.Grid }, discharge, Start));
        Assert.Null(Replace(committed, discharge with { Targets = Slot(0, 0).Targets }, Start));
        Assert.Null(Replace(committed, discharge with { ExpectedConsumptionKwh = .32m }, Start));
        Assert.Null(Replace(committed, discharge, now.AddSeconds(-76)));
        Assert.Null(Replace(committed, discharge, now.AddSeconds(1)));
        Assert.Null(Replace(committed, discharge with { TimeSlot = new(Start.AddMinutes(15), Start.AddMinutes(30)) }, Start));
        Assert.Null(Replace(committed with { ExpectedConsumptionKwh = 1 }, discharge, Start));
        Assert.Null(JointBatterySlotCommitment.NetDemandReplacement(committed, null, now));
        Assert.Null(JointBatterySlotCommitment.NetDemandReplacement(committed, Plan(discharge), Start.AddMinutes(15)));
    }
    [Theory]
    [InlineData(0, -2200, -728, -2400)]
    [InlineData(-1500, -1500, -500, -200)]
    [InlineData(3000, 500, 1000, 2200)]
    [InlineData(1000, 2000, 0, 500)]
    public void SameActionUpdatesPowerAndAllocation(int oldA, int oldB, int newA, int newB)
    {
        var fresh = Slot(newA, newB) with { Reason = "Aktueller Bedarf" };
        var updated = JointBatterySlotCommitment.UpdatedTargets(Slot(oldA, oldB), Plan(fresh), Start.AddSeconds(30));
        Assert.Same(fresh, updated);
    }

    [Theory]
    [InlineData(-1000, 0, 1000, 0)]
    [InlineData(1000, 0, -1000, 0)]
    [InlineData(-1000, 0, 0, 0)]
    [InlineData(1000, 0, 0, 0)]
    [InlineData(0, 0, -1000, 0)]
    [InlineData(0, 0, 1000, 0)]
    [InlineData(-1000, 0, -2000, 500)]
    public void DifferentBasicActionOrOpposingTargetsAreNotAccepted(int oldA, int oldB, int newA, int newB)
    {
        Assert.Null(JointBatterySlotCommitment.UpdatedTargets(Slot(oldA, oldB), Plan(Slot(newA, newB)), Start.AddSeconds(30)));
    }

    [Fact]
    public void UpdatingTargetsRequiresCurrentMatchingPlanAndPreservesChargingSource()
    {
        var committed = Slot(1000, 500) with { ChargeSource = BatteryChargeSource.PV };
        var fresh = Slot(500, 1000) with { ChargeSource = BatteryChargeSource.PV };
        var now = Start.AddSeconds(30);
        Assert.Same(fresh, JointBatterySlotCommitment.UpdatedTargets(committed, Plan(fresh), now));
        Assert.Null(JointBatterySlotCommitment.UpdatedTargets(committed, Plan(fresh with { ChargeSource = BatteryChargeSource.Grid }), now));
        Assert.Null(JointBatterySlotCommitment.UpdatedTargets(committed, Plan(fresh) with { CalculatedAtUtc = now.AddSeconds(-76) }, now));
        Assert.Null(JointBatterySlotCommitment.UpdatedTargets(committed, Plan(fresh) with { CalculatedAtUtc = now.AddSeconds(1) }, now));
        Assert.Null(JointBatterySlotCommitment.UpdatedTargets(committed, Plan(fresh), Start.AddMinutes(15)));
        Assert.Null(JointBatterySlotCommitment.UpdatedTargets(committed, null, now));
        Assert.Null(JointBatterySlotCommitment.UpdatedTargets(committed, Plan(fresh with { TimeSlot = new(Start.AddMinutes(15), Start.AddMinutes(30)) }), now));
        Assert.Null(JointBatterySlotCommitment.UpdatedTargets(committed, Plan(fresh with { Targets = [fresh.Targets[0]] }), now));
        Assert.Null(JointBatterySlotCommitment.UpdatedTargets(Slot(1000, 500), Plan(Slot(500, 1000) with { IsPriceEstimated = true }), now));
    }
}
