using TibberVictronController.Business.Operations;

namespace TibberVictronController.Business.Tests;

public sealed class OperationsAlertEvaluatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 1, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private DateTimeOffset now = Start;
    private OperationsAlertState state = OperationsAlertState.Empty;

    private static OperationsProblem Problem(string id = "a", int holdMinutes = 10, int recoveryMinutes = 0, DateTimeOffset? knownSince = null) =>
        new(id, "Titel " + id, "Detail " + id + ".", TimeSpan.FromMinutes(holdMinutes), TimeSpan.FromMinutes(recoveryMinutes), knownSince);

    /// <summary>Evaluates at the manual clock's time and delivers every due message unless told otherwise.</summary>
    private OperationsAlertEvaluation Step(TimeSpan advance, bool deliver, params OperationsProblem[] problems)
    {
        now += advance;
        var evaluation = OperationsAlertEvaluator.Evaluate(state, problems, now);
        state = evaluation.State;
        if (deliver)
            foreach (var notification in evaluation.Due) state = OperationsAlertEvaluator.Acknowledge(state, notification, now);
        return evaluation;
    }

    private static TimeSpan Minutes(double minutes) => TimeSpan.FromMinutes(minutes);

    [Fact]
    public void NoAlertBeforeTheHoldTimeHasPassed()
    {
        Assert.Empty(Step(TimeSpan.Zero, true, Problem()).Due);
        var justBefore = Step(Minutes(9.5), true, Problem());

        Assert.Empty(justBefore.Started);
        Assert.Empty(justBefore.Due);
    }

    [Fact]
    public void AShortProblemNeverAlertsAndLeavesNoState()
    {
        Step(TimeSpan.Zero, true, Problem());
        Step(Minutes(0.5), true, Problem());
        var gone = Step(Minutes(0.5), true);

        Assert.Empty(gone.Started);
        Assert.Empty(gone.Ended);
        Assert.Empty(gone.Due);
        Assert.Empty(state.Alerts);
    }

    [Fact]
    public void AlertsOnceWhenTheHoldTimeHasPassed()
    {
        Step(TimeSpan.Zero, true, Problem());
        var raised = Step(Minutes(10), true, Problem());
        var next = Step(Minutes(1), true, Problem());

        var started = Assert.Single(raised.Started);
        Assert.Equal("a", started.Id);
        Assert.Equal(Start, started.SinceUtc);
        var due = Assert.Single(raised.Due);
        Assert.Equal(OperationsAlertNotificationKind.Start, due.Kind);
        Assert.Empty(next.Started);
        Assert.Empty(next.Due);
    }

    [Fact]
    public void AProblemWithoutHoldTimeAlertsAtOnce()
    {
        var raised = Step(TimeSpan.Zero, true, Problem(holdMinutes: 0));

        Assert.Equal(OperationsAlertNotificationKind.Start, Assert.Single(raised.Due).Kind);
    }

    [Fact]
    public void AKnownBeginningCountsForTheHoldTimeAndIsReported()
    {
        // The source knows the last received message: five minutes of silence have already passed at the first look.
        var raised = Step(TimeSpan.Zero, true, Problem(holdMinutes: 5, knownSince: Start.AddMinutes(-6)));

        Assert.Equal(Start.AddMinutes(-6), Assert.Single(raised.Due).Alert.SinceUtc);
    }

    [Fact]
    public void RemindsEverySixHoursAndNotEarlier()
    {
        Step(TimeSpan.Zero, true, Problem(holdMinutes: 0));
        var before = Step(TimeSpan.FromHours(5.9), true, Problem(holdMinutes: 0));
        var reminder = Step(TimeSpan.FromHours(0.1), true, Problem(holdMinutes: 0));
        var after = Step(Minutes(1), true, Problem(holdMinutes: 0));
        var second = Step(TimeSpan.FromHours(6), true, Problem(holdMinutes: 0));

        Assert.Empty(before.Due);
        Assert.Equal(OperationsAlertNotificationKind.Reminder, Assert.Single(reminder.Due).Kind);
        Assert.Equal(Start, reminder.Due[0].Alert.SinceUtc);
        Assert.Empty(reminder.Started);
        Assert.Empty(after.Due);
        Assert.Equal(OperationsAlertNotificationKind.Reminder, Assert.Single(second.Due).Kind);
    }

    [Fact]
    public void ReportsTheEndOnceAndForgetsTheAlert()
    {
        Step(TimeSpan.Zero, true, Problem(holdMinutes: 0));
        var over = Step(Minutes(20), true);
        var later = Step(Minutes(1), true);

        var ended = Assert.Single(over.Ended);
        Assert.Equal(now.AddMinutes(-1), ended.EndedAtUtc);
        var due = Assert.Single(over.Due);
        Assert.Equal(OperationsAlertNotificationKind.End, due.Kind);
        Assert.Equal(Start, due.Alert.SinceUtc);
        Assert.Empty(later.Ended);
        Assert.Empty(later.Due);
        Assert.Empty(state.Alerts);
    }

    [Fact]
    public void AfterItsEndTheSameProblemStartsANewAlertWithANewHoldTime()
    {
        Step(TimeSpan.Zero, true, Problem());
        Step(Minutes(10), true, Problem());
        Step(Minutes(1), true);
        var back = Step(Minutes(1), true, Problem());
        var raisedAgain = Step(Minutes(10), true, Problem());

        Assert.Empty(back.Due);
        Assert.Equal(OperationsAlertNotificationKind.Start, Assert.Single(raisedAgain.Due).Kind);
        Assert.Equal(Start.AddMinutes(12), raisedAgain.Due[0].Alert.SinceUtc);
    }

    [Fact]
    public void SimultaneousProblemsAreSeparateAlertsWithStableIds()
    {
        Step(TimeSpan.Zero, true, Problem("a", 0), Problem("b", 10));
        var both = Step(Minutes(10), true, Problem("a", 0), Problem("b", 10));
        var oneOver = Step(Minutes(1), true, Problem("b", 10));

        Assert.Equal("b", Assert.Single(both.Started).Id);
        Assert.Equal("b", Assert.Single(both.Due).Alert.Id);
        Assert.Equal("a", Assert.Single(oneOver.Ended).Id);
        Assert.Equal((OperationsAlertNotificationKind.End, "a"), (oneOver.Due[0].Kind, Assert.Single(oneOver.Due).Alert.Id));
        Assert.Equal(new[] { "b" }, state.Alerts.Keys);
    }

    [Fact]
    public void AnUndeliveredStartStaysDueWithoutStartingTwice()
    {
        var first = Step(TimeSpan.Zero, false, Problem(holdMinutes: 0));
        var retry = Step(Minutes(1), false, Problem(holdMinutes: 0));
        var delivered = Step(Minutes(1), true, Problem(holdMinutes: 0));
        var quiet = Step(Minutes(1), true, Problem(holdMinutes: 0));

        Assert.Single(first.Started);
        Assert.Empty(retry.Started);
        Assert.Equal(OperationsAlertNotificationKind.Start, Assert.Single(retry.Due).Kind);
        Assert.Equal(OperationsAlertNotificationKind.Start, Assert.Single(delivered.Due).Kind);
        Assert.Empty(quiet.Due);
    }

    [Fact]
    public void TheReminderIntervalCountsFromTheDeliveredMessage()
    {
        Step(TimeSpan.Zero, false, Problem(holdMinutes: 0));
        // Delivered one hour late.
        Step(TimeSpan.FromHours(1), true, Problem(holdMinutes: 0));
        var sixHoursAfterBeginning = Step(TimeSpan.FromHours(5), true, Problem(holdMinutes: 0));
        var sixHoursAfterDelivery = Step(TimeSpan.FromHours(1), true, Problem(holdMinutes: 0));

        Assert.Empty(sixHoursAfterBeginning.Due);
        Assert.Equal(OperationsAlertNotificationKind.Reminder, Assert.Single(sixHoursAfterDelivery.Due).Kind);
    }

    [Fact]
    public void AnUndeliveredReminderIsOfferedAgain()
    {
        Step(TimeSpan.Zero, true, Problem(holdMinutes: 0));
        Step(TimeSpan.FromHours(6), false, Problem(holdMinutes: 0));
        var retry = Step(Minutes(1), true, Problem(holdMinutes: 0));
        var quiet = Step(Minutes(1), true, Problem(holdMinutes: 0));

        Assert.Equal(OperationsAlertNotificationKind.Reminder, Assert.Single(retry.Due).Kind);
        Assert.Empty(quiet.Due);
    }

    [Fact]
    public void AnUndeliveredEndStaysDueAndIsGivenUpAfterADay()
    {
        Step(TimeSpan.Zero, true, Problem(holdMinutes: 0));
        var over = Step(Minutes(5), false);
        var retry = Step(Minutes(1), false);
        var givenUp = Step(TimeSpan.FromHours(24), false);

        Assert.Single(over.Ended);
        Assert.Empty(retry.Ended);
        Assert.Equal(OperationsAlertNotificationKind.End, Assert.Single(retry.Due).Kind);
        Assert.Equal(over.Due[0].Alert.EndedAtUtc, retry.Due[0].Alert.EndedAtUtc);
        Assert.Empty(givenUp.Due);
        Assert.Empty(state.Alerts);
    }

    [Fact]
    public void AnAlertNobodyWasToldAboutEndsWithoutAMessage()
    {
        Step(TimeSpan.Zero, false, Problem(holdMinutes: 0));
        var over = Step(Minutes(5), false);

        // The event log still gets the end; people get no "in order again" for something they never heard of.
        Assert.Single(over.Ended);
        Assert.Empty(over.Due);
        Assert.Empty(state.Alerts);
    }

    [Fact]
    public void AProblemReturningBeforeItsEndWasDeliveredContinuesTheAlert()
    {
        Step(TimeSpan.Zero, true, Problem(holdMinutes: 0));
        Step(Minutes(5), false);
        var back = Step(Minutes(1), true, Problem(holdMinutes: 0));

        // The people still have the alert on their devices: no end, no second start message.
        Assert.Empty(back.Due);
        Assert.Single(back.Started);
        Assert.Equal(Start, state.Alerts["a"].SinceUtc);
        Assert.Null(state.Alerts["a"].EndedAtUtc);
    }

    [Fact]
    public void AShortGapWithinTheRecoveryTimeNeitherEndsNorRestartsAnAlert()
    {
        var failing = Problem(holdMinutes: 10, recoveryMinutes: 3);
        Step(TimeSpan.Zero, true, failing);
        Step(Minutes(4), true, failing);
        // One good sample between two failed attempts, before the hold time has passed.
        Step(Minutes(1), true);
        var raised = Step(Minutes(5), true, failing);
        var gap = Step(Minutes(1), true);
        var stillFailing = Step(Minutes(1), true, failing);

        Assert.Equal(Start, Assert.Single(raised.Due).Alert.SinceUtc);
        Assert.Empty(gap.Ended);
        Assert.Empty(gap.Due);
        Assert.Empty(stillFailing.Started);
        Assert.Empty(stillFailing.Due);
    }

    [Fact]
    public void TheAlertEndsOnceTheProblemIsAbsentForTheRecoveryTime()
    {
        var failing = Problem(holdMinutes: 0, recoveryMinutes: 3);
        Step(TimeSpan.Zero, true, failing);
        var tooEarly = Step(Minutes(2), true);
        var over = Step(Minutes(1), true);

        Assert.Empty(tooEarly.Due);
        Assert.Equal(OperationsAlertNotificationKind.End, Assert.Single(over.Due).Kind);
    }

    [Fact]
    public void TheNewestTextOfALastingProblemIsUsed()
    {
        Step(TimeSpan.Zero, true, Problem(holdMinutes: 0));
        var reminder = Step(TimeSpan.FromHours(6), true, Problem(holdMinutes: 0) with { Detail = "Neuer Stand." });

        Assert.Equal("Neuer Stand.", Assert.Single(reminder.Due).Alert.Detail);
    }

    [Fact]
    public void MessagesSayWhatIsWrongAndSinceWhenInLocalTime()
    {
        // 01:00 UTC on 15 November is 02:00 in Berlin (winter time).
        var alert = new OperationsAlert("a", "Akkusteuerung steht", "Die Steuerung steuert nicht.", Start, Start.AddHours(23));

        var start = OperationsAlertMessage.Compose(new(OperationsAlertNotificationKind.Start, alert), Berlin);
        var reminder = OperationsAlertMessage.Compose(new(OperationsAlertNotificationKind.Reminder, alert), Berlin);
        var end = OperationsAlertMessage.Compose(new(OperationsAlertNotificationKind.End, alert), Berlin);

        Assert.Equal(new OperationsAlertMessage.Text("Akkusteuerung steht", "Die Steuerung steuert nicht. Störung seit 15.11. 02:00 Uhr."), start);
        Assert.Equal(new OperationsAlertMessage.Text("Akkusteuerung steht", "Besteht weiterhin: Die Steuerung steuert nicht. Störung seit 15.11. 02:00 Uhr."), reminder);
        Assert.Equal(new OperationsAlertMessage.Text("Wieder in Ordnung: Akkusteuerung steht",
            "Akkusteuerung steht: wieder in Ordnung seit 16.11. 01:00 Uhr. Die Störung bestand seit 15.11. 02:00 Uhr."), end);
    }

    [Fact]
    public void MessagesUseSummerTimeInSummer()
    {
        var alert = new OperationsAlert("a", "Titel", "Detail.", new DateTimeOffset(2026, 7, 1, 22, 30, 0, TimeSpan.Zero));

        var start = OperationsAlertMessage.Compose(new(OperationsAlertNotificationKind.Start, alert), Berlin);

        Assert.Equal("Detail. Störung seit 02.07. 00:30 Uhr.", start.Body);
    }
}
