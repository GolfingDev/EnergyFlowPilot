using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class EvChargingDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 5, 0, TimeSpan.Zero);

    [Fact]
    public void WithoutAPlanTheWallboxChargesAtFullPower()
    {
        var decision = EvChargingDecider.Decide(new EvChargingDecisionInput(Now, null, "Preise fehlen", 0));

        Assert.Equal(EvChargingMode.FullPowerFallback, decision.Mode);
        Assert.Null(decision.Amps);
        Assert.Contains("Preise fehlen", decision.Reason);
    }

    [Fact]
    public void PlanWithoutPriceDataAlsoMeansFullPower()
    {
        var empty = new EvChargePlan(Now, [], 0, 0, 0, [], HasInputSlots: false);

        Assert.Equal(EvChargingMode.FullPowerFallback, EvChargingDecider.Decide(new EvChargingDecisionInput(Now, empty, null, null)).Mode);
    }

    [Fact]
    public void PauseWhileWaitingForPricesSaysSo()
    {
        // Operator 2026-11-11: "no charging needed" was wrong while the plan only waited for the night's prices.
        var waiting = new EvChargePlan(Now, [], 0, 0, 0, [])
        {
            Goals = [new EvChargeGoal(EvChargeReason.BaseSoc, 30, null, null, null) { WaitsForPrices = true }]
        };

        var decision = EvChargingDecider.Decide(new EvChargingDecisionInput(Now, waiting, null, null));

        Assert.Equal(EvChargingMode.Paused, decision.Mode);
        Assert.Contains("wartet auf die Preise", decision.Reason);
    }

    [Fact]
    public void PlannedQuarterHourSetsItsCurrent()
    {
        var decision = EvChargingDecider.Decide(new EvChargingDecisionInput(Now, Plan(gridKwh: 2.75, powerWatts: 11000), null, null));

        Assert.Equal(EvChargingMode.Plan, decision.Mode);
        Assert.Equal(16, decision.Amps);
    }

    [Fact]
    public void PvQuarterHourForATripChargesAsPlannedAheadOfTheHomeBatteries()
    {
        // No live surplus: the home batteries take the PV. A trip slot still charges (operator 2026-11-06).
        var start = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        EvChargePlan PvSlot(EvChargeReason reason) => new(Now,
            [new EvPlannedChargeSlot(start, start.AddMinutes(15), 6900, 0, 1.7, 0.30, false, reason)], 0, 1.7, 0, []);

        var trip = EvChargingDecider.Decide(new EvChargingDecisionInput(Now, PvSlot(EvChargeReason.Trip), null, 0));
        var surplus = EvChargingDecider.Decide(new EvChargingDecisionInput(Now, PvSlot(EvChargeReason.PvSurplus), null, 0));

        Assert.Equal(EvChargingMode.Plan, trip.Mode);
        Assert.Equal(10, trip.Amps);
        Assert.Contains("für eine Fahrt", trip.Reason);
        Assert.Equal(EvChargingMode.Paused, surplus.Mode);
    }

    [Theory]
    [InlineData(5000, 0, 6)]      // (5000 - 300) / 690 = 6.8 -> 6 A without grid
    [InlineData(9000, 0, 12)]     // 8700 / 690 = 12.6 -> 12 A
    [InlineData(3500, 1000, 6)]   // below the minimum, 6 A take at most ~640 W from the grid
    public void SurplusCurrentFollowsThePv(double available, double allowance, double amps)
    {
        var decision = EvChargingDecider.Decide(new EvChargingDecisionInput(Now, Plan(0, 0), null, available) { PvGridAllowanceWatts = allowance });

        Assert.Equal(EvChargingMode.PvSurplus, decision.Mode);
        Assert.Equal(amps, decision.Amps);
    }

    [Fact]
    public void NothingPlannedAndTooLittleSurplusPauses()
    {
        var decision = EvChargingDecider.Decide(new EvChargingDecisionInput(Now, Plan(0, 0), null, 3000) { PvGridAllowanceWatts = 500 });

        Assert.Equal(EvChargingMode.Paused, decision.Mode);
        Assert.Equal(0, decision.Amps);
    }

    private static EvChargePlan Plan(double gridKwh, double powerWatts)
    {
        var start = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var slots = gridKwh > 0
            ? new[] { new EvPlannedChargeSlot(start, start.AddMinutes(15), powerWatts, gridKwh, 0, 0.12, false, EvChargeReason.Trip) }
            : [];
        return new EvChargePlan(Now, slots, gridKwh, 0, 0, []);
    }
}
