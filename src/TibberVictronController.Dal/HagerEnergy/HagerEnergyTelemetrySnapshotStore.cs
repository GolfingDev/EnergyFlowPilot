namespace TibberVictronController.Dal.HagerEnergy;

/// <summary>
/// Singleton in-memory store for the latest E3/DC telemetry snapshot.
/// Updated by the polling background service; read by the dashboard endpoint.
/// </summary>
public sealed class HagerEnergyTelemetrySnapshotStore
{
    private volatile HagerEnergyTelemetrySnapshot? snapshot;

    public HagerEnergyTelemetrySnapshot? Current => snapshot;

    public void Update(HagerEnergyCurrentValues values)
    {
        snapshot = new HagerEnergyTelemetrySnapshot(
            GridImportWatts: (int)Math.Round(values.GridImportWatts, MidpointRounding.AwayFromZero),
            PvProductionWatts: (int)Math.Round(values.PvProductionWatts, MidpointRounding.AwayFromZero),
            BatterySocPercent: (int)Math.Round(values.BatterySocPercent, MidpointRounding.AwayFromZero),
            MeasuredAtUtc: values.MeasuredAtUtc,
            LastSuccessfulPollAtUtc: DateTimeOffset.UtcNow);
    }
}

public sealed record HagerEnergyTelemetrySnapshot(
    int GridImportWatts,
    int PvProductionWatts,
    int BatterySocPercent,
    DateTimeOffset MeasuredAtUtc,
    DateTimeOffset LastSuccessfulPollAtUtc);
