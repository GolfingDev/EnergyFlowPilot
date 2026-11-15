using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

/// <summary>
/// Pins the economic rules of the joint planner as they are today (table "Which planning rules apply to which path" in
/// docs/20-domain/battery-decision-engine.md). Where a rule differs from the single-battery path, the test says so; a
/// later, deliberate alignment (audit 2026-11-11, step D7) changes these expectations together with the documentation.
/// </summary>
public sealed class JointBatteryPlannerRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 17, 0, 0, TimeSpan.Zero);

    private static JointBatteryUnit Unit(string id, decimal soc, decimal planningMaximum = 95) =>
        new(id, new BatteryConfiguration(new BatteryConfigurationValues { TotalCapacityKwh = 10,
            MinimumStateOfChargePercent = 10, PlanningMinimumStateOfChargePercent = 10,
            PlanningMaximumStateOfChargePercent = planningMaximum, MaximumChargePowerWatts = 3000,
            MaximumDischargePowerWatts = 3000, RoundTripEfficiencyPercent = 90 }), new BatteryState(soc, Now), 0);

    private static ForecastTimeSlot[] Slots(int count) => Enumerable.Range(0, count)
        .Select(i => new ForecastTimeSlot(Now.AddMinutes(15 * i), Now.AddMinutes(15 * (i + 1)))).ToArray();

    private static JointBatteryPlan Plan(IReadOnlyList<JointBatteryUnit> batteries, ForecastTimeSlot[] slots, Func<int, decimal> price,
        Func<int, decimal> consumptionKwh, Func<int, decimal>? pvKwh = null) =>
        new JointBatteryPlanner().Calculate(new JointBatteryPlanInput {
            NowUtc = Now, Batteries = batteries,
            Prices = slots.Select((time, i) => new TibberPriceForecastSlot(time, price(i), "EUR")).ToArray(),
            Pv = slots.Select((time, i) => new PvYieldForecastSlot(time, pvKwh?.Invoke(i) ?? 0)).ToArray(),
            Consumption = slots.Select((time, i) => new ConsumptionForecastSlot(time, consumptionKwh(i))).ToArray(),
            MaximumGridImportWatts = 0, FeedInCompensationPerKwh = 0.08m
        });

    [Fact]
    public void ScarceEnergyIsHeldForALaterSlotThatIsOnlySlightlyMoreExpensive()
    {
        // 0.2 ct/kWh later advantage. The single-battery path discharges now (its 3 ct hurdle applies to holding);
        // the joint planner has no hurdle on holding and keeps the energy for the later slots.
        var slots = Slots(8);
        var plan = Plan([Unit("victron", 15), Unit("e3dc", 15)], slots, i => i == 0 ? 0.300m : 0.302m, _ => 0.25m);

        Assert.Equal(0, plan.Slots[0].Targets.Sum(t => t.SignedPowerWatts));
        Assert.Contains(plan.Slots.Skip(1), slot => slot.Targets.Sum(t => t.SignedPowerWatts) < 0);
    }

    [Fact]
    public void NegativePriceGridChargingStopsAtThePlanningMaximum()
    {
        // The single-battery path may charge above the planning maximum when the price is below the feed-in
        // compensation; the joint planner never grid-charges above it.
        var slots = Slots(2);
        var plan = Plan([Unit("victron", 80), Unit("e3dc", 80)], slots, _ => -0.20m, _ => 0m);

        Assert.All(plan.Slots, slot => Assert.All(slot.Targets, target => Assert.True(target.SocAfterPercent <= 95.01m)));
        Assert.Contains(plan.Slots[0].Targets, target => target.SignedPowerWatts > 0);
    }

    [Fact]
    public void MissingPvForecastSetToZeroCanJustifyGridChargingThatRealPvWouldMakeUnnecessary()
    {
        // Audit finding K2: JointPlanInputFallback.CompletePv uses 0 kWh for slots without any forecast. With a cheap
        // slot now and expensive slots later, the planner then buys energy that the PV would have delivered.
        var slots = Slots(6);
        decimal Price(int i) => i == 0 ? 0.10m : 0.40m;
        var withoutPv = Plan([Unit("victron", 12), Unit("e3dc", 12)], slots, Price, _ => 0.25m);
        var withPv = Plan([Unit("victron", 12), Unit("e3dc", 12)], slots, Price, _ => 0.25m, i => i == 0 ? 0m : 0.5m);

        Assert.Equal(BatteryChargeSource.Grid, withoutPv.Slots[0].ChargeSource);
        Assert.True(withoutPv.Slots[0].Targets.Sum(t => t.SignedPowerWatts) > 0);
        Assert.NotEqual(BatteryChargeSource.Grid, withPv.Slots[0].ChargeSource);
    }

    [Fact]
    public void PvMissingFromEveryForecastIsReportedAndSetToZero()
    {
        var slots = Slots(3);
        var prices = slots.Select(time => new TibberPriceForecastSlot(time, 0.30m, "EUR")).ToArray();
        var cached = new[] { new PvYieldForecastSlot(slots[1], 0.4m) };

        var (pv, fromCache, missing) = JointPlanInputFallback.CompletePv(prices, fresh: null, cached);

        Assert.Equal(1, fromCache);
        Assert.Equal(2, missing);
        Assert.Equal(0.4m, pv[1].ExpectedPvYieldKwh);
        Assert.All(new[] { pv[0], pv[2] }, slot => { Assert.Equal(0m, slot.ExpectedPvYieldKwh); Assert.False(slot.IsForecastAvailable); });
    }

    [Fact]
    public void DischargeThatCoversTheWholeAssumedDemandIsMarkedAsDemandFollowing()
    {
        // Plenty of energy and no later use for it: 1 kW net demand is covered completely in every slot.
        var plan = Plan([Unit("victron", 80), Unit("e3dc", 80)], Slots(4), _ => 0.30m, _ => 0.25m);

        Assert.All(plan.Slots, slot =>
        {
            Assert.True(slot.FollowsDemand);
            Assert.InRange(slot.Targets.Sum(t => t.SignedPowerWatts), -1000, -998);
            Assert.Null(slot.ChargeSource);
        });
    }

    [Fact]
    public void DeliberatePartialDischargeIsNotMarked()
    {
        // About 0.76 kWh deliverable against 0.5 kWh (2 kW) demand per slot: the cheaper first slot gets only part,
        // the rest is kept for the expensive second slot. Raising the first slot would take that energy away.
        var plan = Plan([Unit("victron", 14), Unit("e3dc", 14)], Slots(2), i => i == 0 ? 0.30m : 0.50m, _ => 0.5m);

        Assert.InRange(plan.Slots[0].Targets.Sum(t => t.SignedPowerWatts), -1500, -500);
        Assert.All(plan.Slots[0].Targets, target => Assert.True(target.SocAfterPercent > 11));
        Assert.False(plan.Slots[0].FollowsDemand);
        // The second slot empties both batteries to their reserve and still leaves demand uncovered: not marked either.
        Assert.InRange(plan.Slots[1].Targets.Sum(t => t.SignedPowerWatts), -1990, -1500);
        Assert.False(plan.Slots[1].FollowsDemand);
    }

    [Fact]
    public void OneBatteryCoveringTheDemandAloneIsMarkedAndTheOtherStaysAtZero()
    {
        var plan = Plan([Unit("victron", 10), Unit("e3dc", 80)], Slots(2), _ => 0.30m, _ => 0.25m);

        Assert.Equal(0, plan.Slots[0].Targets[0].SignedPowerWatts);
        Assert.InRange(plan.Slots[0].Targets[1].SignedPowerWatts, -1000, -999);
        Assert.True(plan.Slots[0].FollowsDemand);
    }

    [Fact]
    public void ShortfallAtThePowerLimitOrAtTheReserveIsNotMarked()
    {
        // 8 kW net demand against 2 x 3 kW: the batteries deliver all they can, the rest is planned grid import.
        var powerLimited = Plan([Unit("victron", 80), Unit("e3dc", 80)], Slots(1), _ => 0.30m, _ => 2m);
        // 1 % above the reserve each: the last energy goes out, most of the 4 kW demand stays on the grid.
        var atReserve = Plan([Unit("victron", 11), Unit("e3dc", 11)], Slots(1), _ => 0.30m, _ => 1m);
        // Nothing above the reserve at all: hold.
        var empty = Plan([Unit("victron", 10), Unit("e3dc", 10)], Slots(1), _ => 0.30m, _ => 1m);

        Assert.InRange(powerLimited.Slots[0].Targets.Sum(t => t.SignedPowerWatts), -6000, -5990);
        Assert.False(powerLimited.Slots[0].FollowsDemand);
        Assert.InRange(atReserve.Slots[0].Targets.Sum(t => t.SignedPowerWatts), -1000, -1);
        Assert.False(atReserve.Slots[0].FollowsDemand);
        Assert.Equal(0, empty.Slots[0].Targets.Sum(t => t.SignedPowerWatts));
        Assert.False(empty.Slots[0].FollowsDemand);
    }

    [Fact]
    public void HoldGridChargeAndPvChargeAreNeverMarked()
    {
        // Hold: scarce energy is kept for the slightly more expensive later slots.
        var hold = Plan([Unit("victron", 15), Unit("e3dc", 15)], Slots(8), i => i == 0 ? 0.300m : 0.302m, _ => 0.25m);
        var gridCharge = Plan([Unit("victron", 12), Unit("e3dc", 12)], Slots(6), i => i == 0 ? 0.10m : 0.40m, _ => 0.25m);
        // PV surplus now, demand without PV afterwards and nearly empty batteries: the surplus is stored.
        var pvCharge = Plan([Unit("victron", 12), Unit("e3dc", 12)], Slots(6), _ => 0.30m, _ => 0.25m, i => i == 0 ? 0.75m : 0m);

        Assert.Equal(0, hold.Slots[0].Targets.Sum(t => t.SignedPowerWatts));
        Assert.False(hold.Slots[0].FollowsDemand);
        Assert.Equal(BatteryChargeSource.Grid, gridCharge.Slots[0].ChargeSource);
        Assert.False(gridCharge.Slots[0].FollowsDemand);
        Assert.Equal(BatteryChargeSource.PV, pvCharge.Slots[0].ChargeSource);
        Assert.False(pvCharge.Slots[0].FollowsDemand);
    }

    [Fact]
    public void GridChargingNeedsMoreThanTheHurdleAndWearCost()
    {
        // 3 ct hurdle + 2 ct wear per grid-charged kWh: a 4 ct spread (after losses less) does not pay, 20 ct does.
        var slots = Slots(6);
        JointBatteryUnit WithCosts(string id) => Unit(id, 12) with { Configuration = new BatteryConfiguration(new BatteryConfigurationValues {
            TotalCapacityKwh = 10, MinimumStateOfChargePercent = 10, PlanningMinimumStateOfChargePercent = 10,
            PlanningMaximumStateOfChargePercent = 95, MaximumChargePowerWatts = 3000, MaximumDischargePowerWatts = 3000,
            RoundTripEfficiencyPercent = 90, MinimumSavingsPerKwh = 0.03m, WearCostPerKwh = 0.02m }) };

        var smallSpread = Plan([WithCosts("victron"), WithCosts("e3dc")], slots, i => i == 0 ? 0.30m : 0.34m, _ => 0.25m);
        var largeSpread = Plan([WithCosts("victron"), WithCosts("e3dc")], slots, i => i == 0 ? 0.20m : 0.40m, _ => 0.25m);

        Assert.NotEqual(BatteryChargeSource.Grid, smallSpread.Slots[0].ChargeSource);
        Assert.Equal(BatteryChargeSource.Grid, largeSpread.Slots[0].ChargeSource);
    }
}
