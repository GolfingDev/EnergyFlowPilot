namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Stores the current runtime state of the Victron MQTT integration for API status reporting.
/// </summary>
public sealed class VictronMqttRuntimeStatus
{
    private readonly Lock syncRoot = new();
    private string connectionState = "NotStarted";
    private string? lastErrorMessage;
    private DateTimeOffset? lastSuccessfulMessageAtUtc;

    public string ConnectionState
    {
        get
        {
            lock (syncRoot)
            {
                return connectionState;
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

    public DateTimeOffset? LastSuccessfulMessageAtUtc
    {
        get
        {
            lock (syncRoot)
            {
                return lastSuccessfulMessageAtUtc;
            }
        }
    }

    public void MarkStarting()
    {
        lock (syncRoot)
        {
            connectionState = "Starting";
        }
    }

    public void MarkConnected()
    {
        lock (syncRoot)
        {
            connectionState = "Connected";
            lastErrorMessage = null;
        }
    }

    public void MarkMessageReceived(DateTimeOffset measuredAtUtc)
    {
        lock (syncRoot)
        {
            connectionState = "Connected";
            lastSuccessfulMessageAtUtc = measuredAtUtc;
            lastErrorMessage = null;
        }
    }

    public void MarkFailed(string errorMessage)
    {
        lock (syncRoot)
        {
            connectionState = "Failed";
            lastErrorMessage = errorMessage;
        }
    }
}
