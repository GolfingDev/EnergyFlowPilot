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
    public void PlannedQuarterHourSetsItsCurrent()
    {
        var decision = EvChargingDecider.Decide(new EvChargingDecisionInput(Now, Plan(gridKwh: 2.75, powerWatts: 11000), null, null));

        Assert.Equal(EvChargingMode.Plan, decision.Mode);
        Assert.Equal(16, decision.Amps);
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

    [Fact]
    public void CheapQuarterHoursFillTheCarBeyondTheTripPlan()
    {
        var slots = Enumerable.Range(0, 16).Select(index => new EvChargeSlotInput(Now.AddMinutes(15 * index - 5), Now.AddMinutes(15 * index + 10),
            index < 8 ? 0.05 : 0.30, false, 0)).ToList();
        var input = new EvChargePlanInput(Now, 70, 80, new EvCarParameters(81, 18, 15, 5, 11000), 0, 0.08, slots, [])
        {
            CheapPricePerKwh = EvCharging.EvChargePlanServiceThreshold(slots, Now, 50)
        };

        var plan = EvChargePlanner.Plan(input);

        // 70 % -> 80 % = 8.1 kWh in the battery = 9 kWh at the wallbox, only in the cheap quarter hours.
        Assert.InRange(plan.GridKwh, 9, 9.3);
        Assert.All(plan.Slots, slot => Assert.Equal(0.05, slot.PricePerKwh));
        Assert.All(plan.Slots, slot => Assert.Equal(EvChargeReason.CheapPrice, slot.Reason));
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

internal static class EvCharging
{
    // Mirrors the API's percentile rule so the planner test does not depend on the API project.
    public static double? EvChargePlanServiceThreshold(IReadOnlyList<EvChargeSlotInput> slots, DateTimeOffset now, double percentile)
    {
        var prices = slots.Where(slot => slot.EndsAtUtc > now && slot.StartsAtUtc < now.AddHours(24)).Select(slot => slot.PricePerKwh).Order().ToArray();
        var index = (int)Math.Clamp(Math.Ceiling(prices.Length * percentile / 100d) - 1, 0, prices.Length - 1);
        return Math.Max(0, prices[index]);
    }
}
