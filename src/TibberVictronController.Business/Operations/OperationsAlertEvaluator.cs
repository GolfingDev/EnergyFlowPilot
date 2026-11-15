namespace TibberVictronController.Business.Operations;

/// <summary>
/// A problem that is present right now. <paramref name="HoldTime"/> is how long it must last before anyone is told;
/// <paramref name="RecoveryTime"/> is how long it must be absent before it counts as over (a control that fails,
/// retries and fails again must not produce "in order again" between two attempts).
/// <paramref name="KnownSinceUtc"/> is set when the source knows the real beginning (last received message); otherwise
/// the first observation counts.
/// </summary>
public sealed record OperationsProblem(string Id, string Title, string Detail, TimeSpan HoldTime, TimeSpan RecoveryTime,
    DateTimeOffset? KnownSinceUtc = null);

public enum OperationsAlertNotificationKind
{
    Start,
    Reminder,
    End
}

/// <summary>An alert as it is told to people: stable id, texts and the beginning of the problem.</summary>
public sealed record OperationsAlert(string Id, string Title, string Detail, DateTimeOffset SinceUtc, DateTimeOffset? EndedAtUtc = null);

public sealed record OperationsAlertNotification(OperationsAlertNotificationKind Kind, OperationsAlert Alert);

/// <summary>One problem the evaluator follows, from the first observation until its end has been told.</summary>
public sealed record OperationsTrackedAlert(string Id, string Title, string Detail, DateTimeOffset SinceUtc, DateTimeOffset LastSeenUtc,
    TimeSpan RecoveryTime)
{
    /// <summary>The hold time has passed: the alert exists, whether or not its message could be delivered yet.</summary>
    public bool Raised { get; init; }

    /// <summary>When the start message was delivered; without it nobody knows the alert, so no end message follows.</summary>
    public DateTimeOffset? StartNotifiedAtUtc { get; init; }

    public DateTimeOffset? LastNotifiedAtUtc { get; init; }

    /// <summary>Set while the end message is still to be delivered.</summary>
    public DateTimeOffset? EndedAtUtc { get; init; }

    public OperationsAlert ToAlert() => new(Id, Title, Detail, SinceUtc, EndedAtUtc);
}

/// <summary>The evaluator's memory between two evaluations. Immutable; lives in the process only.</summary>
public sealed record OperationsAlertState(IReadOnlyDictionary<string, OperationsTrackedAlert> Alerts)
{
    public static OperationsAlertState Empty { get; } = new(new Dictionary<string, OperationsTrackedAlert>(StringComparer.Ordinal));
}

/// <summary>
/// <paramref name="Started"/> and <paramref name="Ended"/> name each alert exactly once (for the event log);
/// <paramref name="Due"/> lists the messages to send now and repeats a message until it is acknowledged.
/// </summary>
public sealed record OperationsAlertEvaluation(OperationsAlertState State, IReadOnlyList<OperationsAlert> Started,
    IReadOnlyList<OperationsAlert> Ended, IReadOnlyList<OperationsAlertNotification> Due);

/// <summary>
/// Decides from the problems present now and its previous state which alerts start, end or are due for a reminder.
/// Pure: the caller sends the messages and reports each delivered one with <see cref="Acknowledge"/>, so a failed
/// delivery stays due and is offered again at the next evaluation.
/// </summary>
public static class OperationsAlertEvaluator
{
    public static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(6);

    /// <summary>An end message that cannot be delivered is given up after this time; it would be stale news.</summary>
    public static readonly TimeSpan EndMessageRetention = TimeSpan.FromHours(24);

    public static OperationsAlertEvaluation Evaluate(OperationsAlertState previous, IReadOnlyCollection<OperationsProblem> problems, DateTimeOffset nowUtc)
    {
        var next = new Dictionary<string, OperationsTrackedAlert>(StringComparer.Ordinal);
        var started = new List<OperationsAlert>();
        var ended = new List<OperationsAlert>();
        var present = new Dictionary<string, OperationsProblem>(StringComparer.Ordinal);
        foreach (var problem in problems) present[problem.Id] = problem;

        foreach (var problem in present.Values)
        {
            previous.Alerts.TryGetValue(problem.Id, out var known);
            // A problem that returns before its end was told simply continues: the people still have the alert.
            var tracked = known is null
                ? new OperationsTrackedAlert(problem.Id, problem.Title, problem.Detail, problem.KnownSinceUtc ?? nowUtc, nowUtc, problem.RecoveryTime)
                : known with
                {
                    Title = problem.Title,
                    Detail = problem.Detail,
                    LastSeenUtc = nowUtc,
                    RecoveryTime = problem.RecoveryTime,
                    SinceUtc = problem.KnownSinceUtc ?? known.SinceUtc,
                    EndedAtUtc = null
                };
            if (!tracked.Raised && nowUtc - tracked.SinceUtc >= problem.HoldTime)
            {
                tracked = tracked with { Raised = true };
                started.Add(tracked.ToAlert());
            }
            // Its end was already reported as an event; the event log gets the new beginning too.
            else if (known?.EndedAtUtc is not null) started.Add(tracked.ToAlert());
            next[tracked.Id] = tracked;
        }

        foreach (var known in previous.Alerts.Values)
        {
            if (present.ContainsKey(known.Id)) continue;
            if (known.EndedAtUtc is { } endedAt)
            {
                // The end is known but its message is still undelivered.
                if (nowUtc - endedAt < EndMessageRetention) next[known.Id] = known;
                continue;
            }
            if (nowUtc - known.LastSeenUtc < known.RecoveryTime)
            {
                // Absent for a moment only: keep it, and keep the hold time running.
                next[known.Id] = known;
                continue;
            }
            if (!known.Raised) continue;
            var over = known with { EndedAtUtc = nowUtc };
            ended.Add(over.ToAlert());
            if (over.StartNotifiedAtUtc is not null) next[over.Id] = over;
        }

        var due = new List<OperationsAlertNotification>();
        foreach (var tracked in next.Values.OrderBy(alert => alert.Id, StringComparer.Ordinal))
        {
            if (tracked.EndedAtUtc is not null)
                due.Add(new(OperationsAlertNotificationKind.End, tracked.ToAlert()));
            else if (tracked.Raised && tracked.StartNotifiedAtUtc is null)
                due.Add(new(OperationsAlertNotificationKind.Start, tracked.ToAlert()));
            else if (tracked.Raised && nowUtc - tracked.LastNotifiedAtUtc >= ReminderInterval)
                due.Add(new(OperationsAlertNotificationKind.Reminder, tracked.ToAlert()));
        }

        return new(new OperationsAlertState(next), started, ended, due);
    }

    /// <summary>Records that a due message was delivered at <paramref name="nowUtc"/>.</summary>
    public static OperationsAlertState Acknowledge(OperationsAlertState state, OperationsAlertNotification notification, DateTimeOffset nowUtc)
    {
        if (!state.Alerts.TryGetValue(notification.Alert.Id, out var tracked)) return state;
        var next = new Dictionary<string, OperationsTrackedAlert>(state.Alerts, StringComparer.Ordinal);
        switch (notification.Kind)
        {
            case OperationsAlertNotificationKind.End:
                next.Remove(tracked.Id);
                break;
            case OperationsAlertNotificationKind.Start:
                next[tracked.Id] = tracked with { StartNotifiedAtUtc = nowUtc, LastNotifiedAtUtc = nowUtc };
                break;
            default:
                next[tracked.Id] = tracked with { LastNotifiedAtUtc = nowUtc };
                break;
        }
        return new OperationsAlertState(next);
    }
}
