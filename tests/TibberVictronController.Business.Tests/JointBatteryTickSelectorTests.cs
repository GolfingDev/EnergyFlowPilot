using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class JointBatteryTickSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 5, 0, TimeSpan.Zero);
    private static readonly ForecastTimeSlot Current = new(Now.AddMinutes(-5), Now.AddMinutes(10));

    private static JointBatterySlot Slot(int victron, int e3dc, BatteryChargeSource? source, bool estimated = false, ForecastTimeSlot? time = null) =>
        new(time ?? Current, 0.25m, "EUR", estimated, source, [new("victron", victron, 50, 52), new("e3dc", e3dc, 50, 51)], "Plan", false);

    private static JointBatteryPlan Plan(JointBatterySlot slot, int ageSeconds = 10) =>
        new(Now.AddSeconds(-ageSeconds), [], [slot], 0, []);

    [Theory]
    [InlineData(null, "Die Planung laeuft noch.")]
    [InlineData("Preisabruf fehlgeschlagen.", "Preisabruf fehlgeschlagen.")]
    public void WithoutPlanBothBatteriesRunSelfConsumptionWithoutGridCharging(string? reason, string cause)
    {
        var selection = JointBatteryTickSelector.Select(new(Now, null, null, reason));

        Assert.Equal(JointBatteryTickOutcome.SelfConsumptionFallback, selection.Outcome);
        Assert.Equal(cause, selection.FallbackCause);
        Assert.True(selection.Command.VictronWatts < 0 && selection.Command.E3dcWatts < 0);
        Assert.Null(selection.Command.ChargeSource);
        Assert.Equal(Now.AddSeconds(15), selection.Command.ValidUntilUtc);
        Assert.Null(selection.CommittedSlot);
    }

    [Fact]
    public void StalePlanOrPlanWithoutTheCurrentQuarterHourFallsBack()
    {
        var stale = JointBatteryTickSelector.Select(new(Now, null, Plan(Slot(1000, 0, BatteryChargeSource.Grid), ageSeconds: 76), null));
        var later = new ForecastTimeSlot(Current.EndsAtUtc, Current.EndsAtUtc.AddMinutes(15));
        var missing = JointBatteryTickSelector.Select(new(Now, null, Plan(Slot(1000, 0, BatteryChargeSource.Grid, time: later)), null));

        Assert.Equal("Die gemeinsame Planung ist veraltet.", stale.FallbackCause);
        Assert.Equal("Der Plan enthaelt die aktuelle Viertelstunde nicht.", missing.FallbackCause);
    }

    [Fact]
    public void EstimatedPricesBlockGridChargingButNotOtherActions()
    {
        var gridCharge = JointBatteryTickSelector.Select(new(Now, null, Plan(Slot(1000, 0, BatteryChargeSource.Grid, estimated: true)), null));
        var discharge = JointBatteryTickSelector.Select(new(Now, null, Plan(Slot(-1000, 0, null, estimated: true)), null));

        Assert.Equal("Geschaetzte Preise erlauben keine Netzladung.", gridCharge.FallbackCause);
        Assert.Equal(JointBatteryTickOutcome.NewCommitment, discharge.Outcome);
    }

    [Fact]
    public void FreshPlanCommitsTheCurrentSlotUntilItsEnd()
    {
        var slot = Slot(1000, 500, BatteryChargeSource.Grid);

        var selection = JointBatteryTickSelector.Select(new(Now, null, Plan(slot), null));

        Assert.Equal(JointBatteryTickOutcome.NewCommitment, selection.Outcome);
        Assert.Same(slot, selection.CommittedSlot);
        Assert.Equal((1000, 500, BatteryChargeSource.Grid, Current.EndsAtUtc), (selection.Command.VictronWatts, selection.Command.E3dcWatts,
            selection.Command.ChargeSource, selection.Command.ValidUntilUtc));
    }

    [Fact]
    public void CommittedQuarterHourTakesNewWattsButKeepsItsBasicAction()
    {
        var committed = Slot(1000, 500, BatteryChargeSource.Grid);

        var updated = JointBatteryTickSelector.Select(new(Now, committed, Plan(Slot(2000, 0, BatteryChargeSource.Grid)), null));
        var opposite = JointBatteryTickSelector.Select(new(Now, committed, Plan(Slot(-1500, -500, null)), null));
        var withoutPlan = JointBatteryTickSelector.Select(new(Now, committed, null, null));

        Assert.Equal(JointBatteryTickOutcome.UpdatedTargets, updated.Outcome);
        Assert.Equal(2000, updated.Command.VictronWatts);
        Assert.Same(committed, updated.PreviousCommittedSlot);
        Assert.Equal(JointBatteryTickOutcome.CommittedDespiteDeviation, opposite.Outcome);
        Assert.Equal(1000, opposite.Command.VictronWatts);
        Assert.Equal(-1500, opposite.DeviatingSlot!.Targets[0].SignedPowerWatts);
        Assert.Equal(JointBatteryTickOutcome.Committed, withoutPlan.Outcome);
        Assert.Same(committed, withoutPlan.CommittedSlot);
    }

    [Fact]
    public void ExpiredCommitmentWithoutUsablePlanFallsBackAndKeepsTheOldSlotAsIs()
    {
        var expired = Slot(1000, 0, BatteryChargeSource.Grid, time: new(Now.AddMinutes(-20), Now.AddMinutes(-5)));

        var selection = JointBatteryTickSelector.Select(new(Now, expired, null, null));

        Assert.Equal(JointBatteryTickOutcome.SelfConsumptionFallback, selection.Outcome);
        Assert.Same(expired, selection.CommittedSlot);
    }
}
