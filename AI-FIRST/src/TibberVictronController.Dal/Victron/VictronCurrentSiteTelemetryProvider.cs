using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Provides current live site telemetry from the latest Victron MQTT snapshot.
/// </summary>
public sealed class VictronCurrentSiteTelemetryProvider : ICurrentSiteTelemetryProvider
{
    private readonly VictronTelemetrySnapshotStore snapshotStore;

    public VictronCurrentSiteTelemetryProvider(VictronTelemetrySnapshotStore snapshotStore)
    {
        this.snapshotStore = snapshotStore;
    }

    public Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = snapshotStore.GetSnapshot();

        if (snapshot.GridPowerWatts is null || snapshot.GridPowerMeasuredAtUtc is null)
        {
            throw new InvalidOperationException("Es liegt noch keine Live-Netzleistung aus Victron MQTT vor.");
        }

        if (snapshot.HouseConsumptionWatts is null || snapshot.HouseConsumptionMeasuredAtUtc is null)
        {
            throw new InvalidOperationException("Es liegt noch kein Live-Hausverbrauch aus Victron MQTT vor.");
        }

        var currentGridImportWatts = DecimalToInt(snapshot.GridPowerWatts.Value);
        var currentPvProductionWatts = snapshot.HouseConsumptionWatts.Value < 0m
            ? DecimalToInt(Math.Abs(snapshot.HouseConsumptionWatts.Value))
            : 0;
        var measuredAtUtc = snapshot.GridPowerMeasuredAtUtc.Value <= snapshot.HouseConsumptionMeasuredAtUtc.Value
            ? snapshot.GridPowerMeasuredAtUtc.Value
            : snapshot.HouseConsumptionMeasuredAtUtc.Value;

        return Task.FromResult(new CurrentSiteTelemetry(
            currentGridImportWatts,
            currentPvProductionWatts,
            measuredAtUtc));
    }

    private static int DecimalToInt(decimal value)
    {
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
