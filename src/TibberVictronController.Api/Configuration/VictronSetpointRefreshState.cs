namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Holds the last hardware-approved Victron setpoint so it can be refreshed without recalculating a decision.
/// </summary>
public sealed class VictronSetpointRefreshState
{
    // Read/written while holding the publication gate. Covers a failed release as well as active control.
    public bool JointControlActive { get; set; }
    private readonly SemaphoreSlim publicationGate = new(1, 1);

    // Serializes complete write batches so a concurrent refresh cannot restore a target stopped by a decision.
    public async Task<IDisposable> AcquirePublicationAsync(CancellationToken cancellationToken)
    {
        await publicationGate.WaitAsync(cancellationToken);
        return new PublicationLease(publicationGate);
    }

    private sealed class PublicationLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

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
    IReadOnlyList<VictronSetpointValue> DesiredSetpoints,
    int SignedBatteryTargetWatts,
    int DesiredSignedBatteryTargetWatts)
{
    public bool RequiresGridChargeProtection { get; init; }
}

public sealed record VictronSetpointValue(string Topic, int Value);
