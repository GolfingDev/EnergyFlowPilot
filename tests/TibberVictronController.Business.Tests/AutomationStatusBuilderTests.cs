using TibberVictronController.Business.Dashboard;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class AutomationStatusBuilderTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BatteryUsesPlannerReasonAndNextChangeAndMarksSimulation()
    {
        var summary = new DashboardSummary(NowUtc,
            new DashboardCurrentState(DashboardActionCategory.Idle, 0, "rule-hold", "Alter Grund", 0.30m, "EUR", NowUtc),
            null, [], [], [])
        {
            Overview = new DashboardDecisionOverview(DashboardActionCategory.GridCharge, "Strom ist jetzt 9 ct günstiger als heute Abend.", 0.21m, "EUR",
                new DashboardNextDecision(NowUtc.AddHours(2), DashboardActionCategory.Discharge, "Abendspitze", 0.38m, "EUR", false))
        };

        var list = AutomationStatusBuilder.Build(new AutomationStatusInput(NowUtc, new BatteryAutomationInput(summary, DryRun: true, false, null), null, null));

        var battery = Assert.Single(list);
        Assert.Equal("battery", battery.Id);
        Assert.False(battery.Enabled);
        Assert.StartsWith("Simulation", battery.State);
        Assert.Equal("Netzladung", battery.Action);
        Assert.Equal("Strom ist jetzt 9 ct günstiger als heute Abend.", battery.Trigger);
        Assert.Equal(NowUtc.AddHours(2), battery.NextChangeAtUtc);
        Assert.Equal("Akku deckt Verbrauch", battery.NextChange);
        Assert.Equal("rule-hold", battery.RuleId);
        Assert.True(battery.SettingInverted);
    }

    [Fact]
    public void JointControlNamesBothBatteriesAndItsStatus()
    {
        var list = AutomationStatusBuilder.Build(new AutomationStatusInput(NowUtc, new BatteryAutomationInput(null, false, true, "Active"), null, null));

        var battery = Assert.Single(list);
        Assert.True(battery.Enabled);
        Assert.Equal("Gemeinsame Steuerung: Aktiv", battery.State);
        Assert.Contains("Akku 2 (E3/DC)", battery.Devices);
        Assert.Equal("Noch keine Entscheidung", battery.Action);
        Assert.Null(battery.Trigger);
    }

    [Fact]
    public void CarChargingShowsModeAmpsReasonProblemAndNextPlannedCharge()
    {
        var ev = new EvAutomationInput(true, EvChargingMode.Plan, 6.5, "Günstigster Zeitraum bis zur Abfahrt.", "Wallbox antwortet nicht.", NowUtc.AddHours(5));

        var item = AutomationStatusBuilder.Build(new AutomationStatusInput(NowUtc, new BatteryAutomationInput(null, false, false, null), ev, null))[1];

        Assert.Equal("ev", item.Id);
        Assert.Equal("Lädt nach Plan", item.State);
        Assert.Equal("Lädt nach Plan · 6,5 A", item.Action);
        Assert.Equal("Günstigster Zeitraum bis zur Abfahrt.", item.Trigger);
        Assert.Equal("Wallbox antwortet nicht.", item.Problem);
        Assert.Equal(NowUtc.AddHours(5), item.NextChangeAtUtc);
    }

    [Fact]
    public void HotWaterAnnouncesWindowStartOrEndDependingOnState()
    {
        var plan = new WeishauptHotWaterPricePlan(NowUtc.AddHours(6), NowUtc.AddHours(1), NowUtc.AddHours(2), 0.2m, 0.35m, 0.1m, 6m, 60, "Günstiges Fenster vor dem Abendbedarf.");
        var battery = new BatteryAutomationInput(null, false, false, null);

        var waiting = AutomationStatusBuilder.Build(new AutomationStatusInput(NowUtc, battery, null, new HotWaterAutomationInput(true, false, false, plan)))[1];
        var raised = AutomationStatusBuilder.Build(new AutomationStatusInput(NowUtc.AddMinutes(90), battery, null, new HotWaterAutomationInput(true, true, false, plan)))[1];
        var off = AutomationStatusBuilder.Build(new AutomationStatusInput(NowUtc, battery, null, new HotWaterAutomationInput(false, false, false, null)))[1];

        Assert.Equal(("Bereit", "Preisfenster beginnt", NowUtc.AddHours(1)), (waiting.State, waiting.NextChange, waiting.NextChangeAtUtc));
        Assert.Equal(("Anhebung aktiv", "Preisfenster endet", NowUtc.AddHours(2)), (raised.State, raised.NextChange, raised.NextChangeAtUtc));
        Assert.Equal("Günstiges Fenster vor dem Abendbedarf.", waiting.Trigger);
        Assert.Equal(("Aus", (bool?)false, (DateTimeOffset?)null), (off.State, off.Enabled, off.NextChangeAtUtc));
    }
}
