namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Holds the last hardware-approved Victron setpoint so it can be refreshed without recalculating a decision.
/// </summary>
public sealed class VictronSetpointRefreshState
{
    private readonly object syncRoot = new();
    private VictronSetpointRefreshSnapshot? snapshot;

    public void Update(VictronSetpointRefreshSnapshot nextSnapshot)
    {
        lock (syncRoot)
        {
            snapshot = nextSnapshot;
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            snapshot = null;
        }
    }

    public bool TryGet(DateTimeOffset utcNow, out VictronSetpointRefreshSnapshot currentSnapshot)
    {
        lock (syncRoot)
        {
            if (snapshot is not null && snapshot.ValidToUtc > utcNow)
            {
                currentSnapshot = snapshot;
                return true;
            }
        }

        currentSnapshot = default!;
        return false;
    }

    public bool TryGetLatest(out VictronSetpointRefreshSnapshot currentSnapshot)
    {
        lock (syncRoot)
        {
            if (snapshot is not null)
            {
                currentSnapshot = snapshot;
                return true;
            }
        }

        currentSnapshot = default!;
        return false;
    }
}

public sealed record VictronSetpointRefreshSnapshot(
    DateTimeOffset ValidToUtc,
    IReadOnlyList<VictronSetpointValue> Setpoints,
    IReadOnlyList<VictronSetpointValue> DesiredSetpoints);

public sealed record VictronSetpointValue(string Topic, int Value);
