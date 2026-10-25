using TibberVictronController.Business.Dashboard;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class JointBatteryDashboardOverviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static JointBatteryPlan Plan()
    {
        var config = new BatteryConfiguration(new BatteryConfigurationValues { TotalCapacityKwh = 10, PlanningMinimumStateOfChargePercent = 10 });
        JointBatterySlot Slot(int index, int watts, decimal price) => new(new(Now.AddMinutes(index * 15), Now.AddMinutes((index + 1) * 15)), price, "EUR", false,
            watts > 0 ? BatteryChargeSource.Grid : null, [new("victron", 0, 10, 10), new("e3dc", watts, 50, 50)], "", false, .1m, .5m);
        return new(Now, [new("victron", config, new(10, Now), 0), new("e3dc", config, new(50, Now), -500)],
            [Slot(0, -500, .27m), Slot(1, 0, .18m), Slot(2, 1000, .15m), Slot(3, -1000, .30m)], 0, []);
    }

    [Fact]
    public void SecondaryDischargeIsShownEvenWhenPrimaryBatteryIsIdle()
    {
        var plan = JointBatteryPlanExplainer.Explain(Plan(), null);
        var execution = new JointBatteryExecutionSnapshot("Active", plan.Slots[0].Reason, Now, true, 0, -500, 0, -480, Now.AddSeconds(30));
        var result = JointBatteryDashboardOverview.Create(plan, execution, Now);
        Assert.Equal(DashboardActionCategory.Discharge, result.Category);
        Assert.Equal(.27m, result.PricePerKwh);
        Assert.Contains("27,00 ct/kWh", result.Reason);
        Assert.NotNull(result.Next);
        Assert.Equal(Now.AddMinutes(15), result.Next.StartsAtUtc);
        Assert.Equal(DashboardActionCategory.Hold, result.Next.Category);
    }

    [Fact]
    public void ExpiredExecutionDoesNotAnnounceAnActiveOrUpcomingAction()
    {
        var result = JointBatteryDashboardOverview.Create(Plan(), new("Active", "Letzter Stand", Now.AddMinutes(-2), true, 0, -500), Now);
        Assert.Equal(DashboardActionCategory.Idle, result.Category);
        Assert.Null(result.Next);
        Assert.Contains("wartet", result.Reason);
    }

    [Fact]
    public void ReasonsExplainPriceAdvantageAndDemandRatherThanTechnicalRules()
    {
        var plan = JointBatteryPlanExplainer.Explain(Plan(), new(.2295m, 20, .27m, 7));
        Assert.Contains("15,00 ct/kWh", plan.Slots[2].Reason);
        Assert.Contains("30,00 ct/kWh", plan.Slots[2].Reason);
        Assert.Contains("historische Preisniveau", plan.Slots[2].Reason);
        Assert.DoesNotContain("Leistungsgrenzen", plan.Slots[2].Reason);
        Assert.Contains("18,00 ct/kWh", plan.Slots[1].Reason);
        Assert.Contains("teurer", plan.Slots[1].Reason);
    }

    [Fact]
    public void ZeroExecutedTargetsAreNotShownAsThePlannedCharge()
    {
        var plan = Plan();
        var future = plan.Slots[2] with { TimeSlot = plan.Slots[0].TimeSlot };
        plan = plan with { Slots = [future] };
        var result = JointBatteryDashboardOverview.Create(plan, new("Active", "Plan sieht Ladung vor", Now, true, 0, 0, 0, 0, Now.AddSeconds(30)), Now);
        Assert.NotEqual(DashboardActionCategory.GridCharge, result.Category);
        Assert.Contains("pausieren", result.Reason);
    }

    [Fact]
    public void ManualChargeDoesNotPretendToBePvChargingOrPromiseAutomaticNextStep()
    {
        var plan = Plan();
        var result = JointBatteryDashboardOverview.Create(plan, new("Active", "Manuelle Ladung angefordert", Now, true, 1000, 1000, 900, 900, Now.AddSeconds(30)), Now, true);
        Assert.Equal(DashboardActionCategory.GridCharge, result.Category);
        Assert.Null(result.Next);
        Assert.Contains("Manuelle", result.Reason);
    }
}
