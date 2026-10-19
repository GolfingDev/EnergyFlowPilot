namespace TibberVictronController.Dal.HagerEnergy;

/// <summary>
/// Singleton in-memory store for the latest E3/DC telemetry snapshot.
/// Updated by the polling background service; read by the dashboard endpoint.
/// </summary>
public sealed class HagerEnergyTelemetrySnapshotStore
{
    private volatile HagerEnergyTelemetrySnapshot? snapshot;

    public HagerEnergyTelemetrySnapshot? Current => snapshot;

    /// <summary>
    /// Gets the last raw API response, used to find additional fields such as external PV inverters.
    /// </summary>
    public string? LastRawJson { get; private set; }

    public void Update(HagerEnergyCurrentValues values)
    {
        LastRawJson = values.RawJson ?? LastRawJson;
        snapshot = new HagerEnergyTelemetrySnapshot(
            GridImportWatts: (int)Math.Round(values.GridImportWatts, MidpointRounding.AwayFromZero),
            PvProductionWatts: (int)Math.Round(values.PvProductionWatts, MidpointRounding.AwayFromZero),
            BatterySocPercent: (int)Math.Round(values.BatterySocPercent, MidpointRounding.AwayFromZero),
            BatteryPowerWatts: values.BatteryPowerWatts.HasValue
                ? (int)Math.Round(values.BatteryPowerWatts.Value, MidpointRounding.AwayFromZero)
                : null,
            MeasuredAtUtc: values.MeasuredAtUtc,
            LastSuccessfulPollAtUtc: DateTimeOffset.UtcNow);
    }
}

public sealed record HagerEnergyTelemetrySnapshot(
    int GridImportWatts,
    int PvProductionWatts,
    int BatterySocPercent,
    int? BatteryPowerWatts,
    DateTimeOffset MeasuredAtUtc,
    DateTimeOffset LastSuccessfulPollAtUtc);
