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

    private static JointBatterySlot Following(int victron, int e3dc) => Slot(victron, e3dc, null) with { FollowsDemand = true };

    [Fact]
    public void NewCommitmentOfADemandFollowingSlotKeepsThePlannedWattsAsSharesAndSaysSo()
    {
        var selection = JointBatteryTickSelector.Select(new(Now, null, Plan(Following(-200, -100)), null));

        Assert.Equal(JointBatteryTickOutcome.NewCommitment, selection.Outcome);
        Assert.True(selection.Command.FollowsDemand);
        Assert.Equal((-200, -100), (selection.Command.VictronWatts, selection.Command.E3dcWatts));
        Assert.Null(selection.Command.ChargeSource);
        Assert.Contains("folgen dabei dem gemessenen Hausbedarf", selection.Command.Reason);
        Assert.True(selection.CommittedSlot!.FollowsDemand);
    }

    [Theory]
    [InlineData(-200, -100, null)]
    [InlineData(-300, 0, null)]
    [InlineData(0, 0, null)]
    [InlineData(1000, 500, BatteryChargeSource.Grid)]
    [InlineData(400, 300, BatteryChargeSource.PV)]
    public void SlotsThatAreNotMarkedProduceExactlyThePreviousCommand(int victron, int e3dc, BatteryChargeSource? source)
    {
        var slot = Slot(victron, e3dc, source);
        var expected = new JointBatteryCommand(victron, e3dc, source, Current.EndsAtUtc, "Plan");

        Assert.Equal(expected, JointBatteryTickSelector.Select(new(Now, null, Plan(slot), null)).Command);
        Assert.Equal(expected, JointBatteryTickSelector.Select(new(Now, slot, null, null)).Command);
        Assert.Equal(expected, JointBatteryTickSelector.Select(new(Now, slot, Plan(slot), null)).Command);
    }

    [Theory]
    [InlineData(1000, 500, BatteryChargeSource.Grid)]
    [InlineData(0, 0, null)]
    public void AMarkOnASlotThatDoesNotDischargeIsIgnored(int victron, int e3dc, BatteryChargeSource? source)
    {
        var slot = Slot(victron, e3dc, source) with { FollowsDemand = true };

        var command = JointBatteryTickSelector.Select(new(Now, null, Plan(slot), null)).Command;

        Assert.Equal(new JointBatteryCommand(victron, e3dc, source, Current.EndsAtUtc, "Plan"), command);
    }

    [Fact]
    public void CommittedDemandFollowingSlotKeepsFollowingWithoutAUsablePlan()
    {
        var committed = Following(-200, -100);
        var later = new ForecastTimeSlot(Current.EndsAtUtc, Current.EndsAtUtc.AddMinutes(15));

        var withoutPlan = JointBatteryTickSelector.Select(new(Now, committed, null, null));
        var stalePlan = JointBatteryTickSelector.Select(new(Now, committed, Plan(Slot(0, 0, null), ageSeconds: 76), null));
        var otherQuarterHour = JointBatteryTickSelector.Select(new(Now, committed, Plan(Slot(0, 0, null, time: later)), null));

        Assert.All(new[] { withoutPlan, stalePlan, otherQuarterHour }, selection =>
        {
            Assert.True(selection.Command.FollowsDemand);
            Assert.Equal((-200, -100), (selection.Command.VictronWatts, selection.Command.E3dcWatts));
            Assert.Same(committed, selection.CommittedSlot);
        });
        Assert.Equal(JointBatteryTickOutcome.Committed, withoutPlan.Outcome);
    }

    [Fact]
    public void FreshSameActionPlanDecidesWhetherTheCommittedQuarterHourFollows()
    {
        var committed = Following(-200, -100);

        // The fresh plan covers the demand it now sees: still "cover the demand", with new shares.
        var stillCovering = JointBatteryTickSelector.Select(new(Now, committed, Plan(Following(-1500, -1300)), null));
        // The fresh plan leaves demand to the grid on purpose: its watts apply, nothing is raised.
        var partial = JointBatteryTickSelector.Select(new(Now, committed, Plan(Slot(-400, 0, null)), null));
        // And back: a committed fixed discharge starts following when the fresh plan covers the demand.
        var newlyCovering = JointBatteryTickSelector.Select(new(Now, Slot(-400, 0, null), Plan(Following(-300, -300)), null));

        Assert.Equal(JointBatteryTickOutcome.UpdatedTargets, stillCovering.Outcome);
        Assert.True(stillCovering.Command.FollowsDemand);
        Assert.Equal(-1500, stillCovering.Command.VictronWatts);
        Assert.Equal(JointBatteryTickOutcome.UpdatedTargets, partial.Outcome);
        Assert.Equal(new JointBatteryCommand(-400, 0, null, Current.EndsAtUtc, "Plan"), partial.Command);
        Assert.True(newlyCovering.Command.FollowsDemand);
    }

    [Fact]
    public void FreshPlanWithAnotherBasicActionKeepsTheCommittedWattsButStopsFollowing()
    {
        var committed = Following(-200, -100);

        // Hold in the fresh plan does not cancel the started discharge (commitment), but its watts are no longer raised.
        var hold = JointBatteryTickSelector.Select(new(Now, committed, Plan(Slot(0, 0, null)), null));
        var charge = JointBatteryTickSelector.Select(new(Now, committed, Plan(Slot(1000, 0, BatteryChargeSource.Grid)), null));
        var smallHold = JointBatteryTickSelector.Select(new(Now, Following(-60, -40), Plan(Slot(0, 0, null)), null));

        var expected = new JointBatteryCommand(-200, -100, null, Current.EndsAtUtc, "Plan");
        Assert.Equal(JointBatteryTickOutcome.CommittedDespiteDeviation, hold.Outcome);
        Assert.Equal(expected, hold.Command);
        Assert.Same(committed, hold.CommittedSlot);
        Assert.Equal(JointBatteryTickOutcome.CommittedDespiteDeviation, charge.Outcome);
        Assert.Equal(expected, charge.Command);
        Assert.Equal(JointBatteryTickOutcome.Committed, smallHold.Outcome);
        Assert.False(smallHold.Command.FollowsDemand);
    }

    [Fact]
    public void VanishedPvSurplusIsReplacedByAFollowingDischargeWhenTheFreshPlanCoversTheDemand()
    {
        var committed = Slot(0, 0, null) with { ExpectedPvKwh = .5m, ExpectedConsumptionKwh = .4m };
        var fresh = Following(-1500, -1200) with { ExpectedPvKwh = .305m, ExpectedConsumptionKwh = 1.0075m };

        var selection = JointBatteryTickSelector.Select(new(Now, committed, Plan(fresh), null));

        Assert.Equal(JointBatteryTickOutcome.NetDemandReplacement, selection.Outcome);
        Assert.True(selection.Command.FollowsDemand);
        Assert.True(selection.CommittedSlot!.FollowsDemand);
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
