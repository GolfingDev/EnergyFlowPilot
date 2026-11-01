using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Ocpp;

/// <summary>
/// Holds the live wallbox state. Every connection gets a generation number: when the wallbox reconnects, the old,
/// still closing connection must not overwrite the state of the new one.
/// </summary>
public sealed class EvChargerStatusStore : IEvChargerStatusSource, IEvChargerControl
{
    private readonly object gate = new();
    private EvChargerSnapshot current = EvChargerSnapshot.Empty;
    private long generation;
    private OcppChargePointSession? session;

    public void Attach(long connectionGeneration, OcppChargePointSession connectionSession)
    {
        lock (gate)
        {
            if (connectionGeneration == generation) session = connectionSession;
        }
    }

    public Task<EvChargerCommandResult> LimitCurrentAsync(double amps, TimeSpan validity, CancellationToken cancellationToken = default)
    {
        if (amps is not 0 and (< 6 or > 16))
            throw new ArgumentOutOfRangeException(nameof(amps), "Der Ladestrom muss 0 A (Pause) oder 6 bis 16 A betragen.");
        if (validity <= TimeSpan.Zero || validity > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(validity), "Eine Begrenzung gilt hoechstens eine Stunde.");
        return ActiveSession().SetCurrentLimitAsync(amps, validity, cancellationToken);
    }

    public Task<EvChargerCommandResult> ClearLimitAsync(CancellationToken cancellationToken = default) =>
        ActiveSession().ClearCurrentLimitAsync(cancellationToken);

    private OcppChargePointSession ActiveSession()
    {
        lock (gate)
        {
            return current.IsConnected && session is not null
                ? session
                : throw new InvalidOperationException("Die Wallbox ist nicht verbunden.");
        }
    }

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
            session = null;
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

    public void RecordRejected(EvConnectionRejection rejection)
    {
        lock (gate)
        {
            current = current with { LastRejection = rejection };
        }
    }
}
