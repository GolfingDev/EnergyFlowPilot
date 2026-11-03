namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Stores the current runtime state of the decision worker for health reporting.
/// </summary>
public sealed class DecisionWorkerRuntimeStatus
{
    private readonly Lock syncRoot = new();
    private string state = "NotStarted";
    private string? lastErrorMessage;
    private DateTimeOffset? lastSuccessfulRunAtUtc;
    private DateTimeOffset? lastFailureAtUtc;
    private DateTimeOffset? currentCycleStartedAtUtc;
    private DateTimeOffset? lastCompletedCycleStartedAtUtc;

    public string State
    {
        get
        {
            lock (syncRoot)
            {
                return state;
            }
        }
    }

    public string? LastErrorMessage
    {
        get
        {
            lock (syncRoot)
            {
                return lastErrorMessage;
            }
        }
    }

    public DateTimeOffset? LastSuccessfulRunAtUtc
    {
        get
        {
            lock (syncRoot)
            {
                return lastSuccessfulRunAtUtc;
            }
        }
    }

    public DateTimeOffset? LastFailureAtUtc
    {
        get
        {
            lock (syncRoot)
            {
                return lastFailureAtUtc;
            }
        }
    }

    /// <summary>
    /// Start time of the last cycle that finished successfully. Settings are read at the start of a cycle,
    /// so a setting saved before this moment is known to be part of the last published decision.
    /// </summary>
    public DateTimeOffset? LastCompletedCycleStartedAtUtc
    {
        get
        {
            lock (syncRoot)
            {
                return lastCompletedCycleStartedAtUtc;
            }
        }
    }

    public void MarkCycleStarted(DateTimeOffset startedAtUtc)
    {
        lock (syncRoot)
        {
            currentCycleStartedAtUtc = startedAtUtc;
        }
    }

    public void MarkStarting()
    {
        lock (syncRoot)
        {
            state = "Starting";
        }
    }

    public void MarkSuccessful(DateTimeOffset completedAtUtc)
    {
        lock (syncRoot)
        {
            state = "Healthy";
            lastSuccessfulRunAtUtc = completedAtUtc;
            lastCompletedCycleStartedAtUtc = currentCycleStartedAtUtc ?? lastCompletedCycleStartedAtUtc;
            lastErrorMessage = null;
        }
    }

    public void MarkFailed(string errorMessage, DateTimeOffset failedAtUtc)
    {
        lock (syncRoot)
        {
            state = "Failed";
            lastFailureAtUtc = failedAtUtc;
            lastErrorMessage = errorMessage;
        }
    }

    public void MarkStopped()
    {
        lock (syncRoot)
        {
            state = "Stopped";
        }
    }
}
