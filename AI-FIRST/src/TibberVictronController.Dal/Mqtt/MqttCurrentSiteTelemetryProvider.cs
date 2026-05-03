using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Mqtt;

/// <summary>
/// Provides current live site telemetry from the latest MQTT snapshot.
/// </summary>
public sealed class MqttCurrentSiteTelemetryProvider : ICurrentSiteTelemetryProvider
{
    private readonly MqttTelemetrySnapshotStore snapshotStore;

    public MqttCurrentSiteTelemetryProvider(MqttTelemetrySnapshotStore snapshotStore)
    {
        this.snapshotStore = snapshotStore;
    }

    public Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = snapshotStore.GetSnapshot();

        if (snapshot.GridPowerWatts is null || snapshot.GridPowerMeasuredAtUtc is null)
        {
            throw new InvalidOperationException("Es liegt noch keine Live-Netzleistung aus MQTT vor.");
        }

        if (snapshot.HouseConsumptionWatts is null || snapshot.HouseConsumptionMeasuredAtUtc is null)
        {
            throw new InvalidOperationException("Es liegt noch kein Live-Hausverbrauch aus MQTT vor.");
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
