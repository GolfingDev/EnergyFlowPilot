using TibberVictronController.Business.Operations;

namespace TibberVictronController.Business.Tests;

public sealed class OperationsAlertConditionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 1, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private const long Gigabyte = 1024L * 1024 * 1024;

    private static readonly OperationsSnapshot Healthy = new()
    {
        JointExecutionExpected = true,
        JointExecutionControlling = true,
        JointExecutionStatusText = "Gemeinsame Ausführung läuft.",
        VictronMqttExpected = true,
        VictronLastMessageAtUtc = Now.AddSeconds(-3),
        DatabaseVolumeFreeBytes = 20 * Gigabyte,
        LastBackupSuccessAtUtc = Now.AddHours(-23)
    };

    private static IReadOnlyList<OperationsProblem> Evaluate(OperationsSnapshot snapshot) => OperationsAlertConditions.Evaluate(snapshot, Now, Berlin);

    [Fact]
    public void AHealthySystemHasNoProblems() => Assert.Empty(Evaluate(Healthy));

    [Fact]
    public void UnknownValuesRaiseNoAlert() =>
        Assert.Empty(Evaluate(Healthy with { DatabaseVolumeFreeBytes = null, LastBackupSuccessAtUtc = null, VictronLastMessageAtUtc = null }));

    [Fact]
    public void JointControlThatDoesNotControlIsAProblemAfterTenMinutes()
    {
        var problem = Assert.Single(Evaluate(Healthy with { JointExecutionControlling = false, JointExecutionStatusText = "Gemeinsame Ausführung wartet; Grund im Dashboard." }));

        Assert.Equal(OperationsAlertConditions.JointExecutionNotControllingId, problem.Id);
        Assert.Equal(TimeSpan.FromMinutes(10), problem.HoldTime);
        Assert.Equal("Akkusteuerung steht", problem.Title);
        Assert.Equal("Die gemeinsame Akkusteuerung ist eingeschaltet, steuert die Akkus aber nicht. Status: Gemeinsame Ausführung wartet; Grund im Dashboard.", problem.Detail);
        Assert.Null(problem.KnownSinceUtc);
    }

    [Fact]
    public void JointControlSwitchedOffIsNoProblem() =>
        Assert.Empty(Evaluate(Healthy with { JointExecutionExpected = false, JointExecutionControlling = false, JointPlanMissing = true }));

    [Fact]
    public void AMissingPlanIsAProblemAfterFifteenMinutesWhileTheExecutionControls()
    {
        var problem = Assert.Single(Evaluate(Healthy with { JointPlanMissing = true }));

        Assert.Equal(OperationsAlertConditions.JointPlanMissingId, problem.Id);
        Assert.Equal(TimeSpan.FromMinutes(15), problem.HoldTime);
        Assert.Contains("Notbetrieb Eigenverbrauch", problem.Detail);
    }

    [Fact]
    public void AStoppedExecutionIsReportedOnceAndNotAlsoAsMissingPlan()
    {
        var problem = Assert.Single(Evaluate(Healthy with { JointExecutionControlling = false, JointPlanMissing = true }));

        Assert.Equal(OperationsAlertConditions.JointExecutionNotControllingId, problem.Id);
    }

    [Fact]
    public void VictronSilenceIsAProblemFromFiveMinutesAndNamesTheLastMessage()
    {
        Assert.Empty(Evaluate(Healthy with { VictronLastMessageAtUtc = Now.AddMinutes(-4.9) }));
        var problem = Assert.Single(Evaluate(Healthy with { VictronLastMessageAtUtc = Now.AddMinutes(-5) }));

        Assert.Equal(OperationsAlertConditions.VictronMqttSilentId, problem.Id);
        Assert.Equal(TimeSpan.Zero, problem.HoldTime);
        Assert.Equal(Now.AddMinutes(-5), problem.KnownSinceUtc);
    }

    [Fact]
    public void VictronSilenceIsNoProblemWhileTheClientWasNeverStarted() =>
        Assert.Empty(Evaluate(Healthy with { VictronMqttExpected = false, VictronLastMessageAtUtc = Now.AddHours(-1) }));

    [Fact]
    public void LessThanTwoGigabytesFreeIsAProblem()
    {
        Assert.Empty(Evaluate(Healthy with { DatabaseVolumeFreeBytes = 2 * Gigabyte }));
        var problem = Assert.Single(Evaluate(Healthy with { DatabaseVolumeFreeBytes = 2 * Gigabyte - 1 - Gigabyte / 2 }));

        Assert.Equal(OperationsAlertConditions.DatabaseDiskLowId, problem.Id);
        Assert.Equal("Auf dem Laufwerk der Datenbank sind nur noch 1,5 GB frei (Grenze 2 GB).", problem.Detail);
    }

    [Fact]
    public void ABackupOlderThanThirtySixHoursIsAProblem()
    {
        Assert.Empty(Evaluate(Healthy with { LastBackupSuccessAtUtc = Now.AddHours(-36) }));
        var problem = Assert.Single(Evaluate(Healthy with { LastBackupSuccessAtUtc = Now.AddHours(-37) }));

        Assert.Equal(OperationsAlertConditions.BackupOverdueId, problem.Id);
        // 13 November 12:00 UTC is 13:00 in Berlin; overdue for one hour.
        Assert.Equal("Die letzte erfolgreiche Datensicherung ist vom 13.11. 13:00 Uhr.", problem.Detail);
        Assert.Equal(Now.AddHours(-1), problem.KnownSinceUtc);
    }

    [Fact]
    public void SeveralProblemsHaveDistinctIds()
    {
        var problems = Evaluate(Healthy with
        {
            JointExecutionControlling = false,
            VictronLastMessageAtUtc = Now.AddHours(-1),
            DatabaseVolumeFreeBytes = 0,
            LastBackupSuccessAtUtc = Now.AddDays(-3)
        });

        Assert.Equal(4, problems.Select(problem => problem.Id).Distinct().Count());
    }
}
