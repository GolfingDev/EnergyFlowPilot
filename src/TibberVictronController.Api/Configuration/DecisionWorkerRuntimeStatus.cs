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
