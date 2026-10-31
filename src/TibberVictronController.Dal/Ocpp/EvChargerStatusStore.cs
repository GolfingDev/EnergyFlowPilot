using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Ocpp;

/// <summary>
/// Holds the live wallbox state. Every connection gets a generation number: when the wallbox reconnects, the old,
/// still closing connection must not overwrite the state of the new one.
/// </summary>
public sealed class EvChargerStatusStore : IEvChargerStatusSource
{
    private readonly object gate = new();
    private EvChargerSnapshot current = EvChargerSnapshot.Empty;
    private long generation;

    public EvChargerSnapshot Current
    {
        get { lock (gate) return current; }
    }

    public long Connect(string chargePointId, DateTimeOffset connectedAtUtc)
    {
        lock (gate)
        {
            generation++;
            current = current with
            {
                IsConnected = true,
                ChargePointId = chargePointId,
                ConnectedSinceUtc = connectedAtUtc,
                LastMessageAtUtc = connectedAtUtc,
                ConfigurationNote = null
            };
            return generation;
        }
    }

    public void Disconnect(long connectionGeneration)
    {
        lock (gate)
        {
            if (connectionGeneration != generation) return;
            // The last reading is history now; a stale power value must not look live to the planner.
            current = current with { IsConnected = false, PowerWatts = null, PowerIsDerived = false };
        }
    }

    public void Update(long connectionGeneration, Func<EvChargerSnapshot, EvChargerSnapshot> change)
    {
        lock (gate)
        {
            if (connectionGeneration == generation) current = change(current);
        }
    }

    public void RecordRejected(string chargePointId, string reason, DateTimeOffset rejectedAtUtc)
    {
        lock (gate)
        {
            current = current with
            {
                LastRejectedChargePointId = chargePointId,
                LastRejectedReason = reason,
                LastRejectedAtUtc = rejectedAtUtc
            };
        }
    }
}
