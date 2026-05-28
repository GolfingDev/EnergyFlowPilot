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

        var effectiveHouseConsumptionWatts = GetEffectiveHouseConsumptionWatts(snapshot);
        var effectiveHouseConsumptionMeasuredAtUtc = GetEffectiveHouseConsumptionMeasuredAtUtc(snapshot);

        if (effectiveHouseConsumptionWatts is null || effectiveHouseConsumptionMeasuredAtUtc is null)
        {
            throw new InvalidOperationException("Es liegt noch kein Live-Hausverbrauch aus MQTT vor.");
        }

        var currentGridImportWatts = DecimalToInt(snapshot.GridPowerWatts.Value);
        var currentBatteryPowerWatts = snapshot.BatteryPowerWatts is null
            ? (int?)null
            : DecimalToInt(snapshot.BatteryPowerWatts.Value);
        var currentPvProductionWatts = effectiveHouseConsumptionWatts.Value < 0m
            ? DecimalToInt(Math.Abs(effectiveHouseConsumptionWatts.Value))
            : 0;
        var measuredAtUtc = snapshot.GridPowerMeasuredAtUtc.Value <= effectiveHouseConsumptionMeasuredAtUtc.Value
            ? snapshot.GridPowerMeasuredAtUtc.Value
            : effectiveHouseConsumptionMeasuredAtUtc.Value;

        return Task.FromResult(new CurrentSiteTelemetry(
            currentGridImportWatts,
            currentPvProductionWatts,
            measuredAtUtc,
            currentBatteryPowerWatts));
    }

    private static decimal? GetEffectiveHouseConsumptionWatts(MqttTelemetrySnapshot snapshot)
    {
        if (snapshot.HouseConsumptionWatts is null or 0m)
        {
            return snapshot.GridPowerWatts;
        }

        return snapshot.HouseConsumptionWatts;
    }

    private static DateTimeOffset? GetEffectiveHouseConsumptionMeasuredAtUtc(MqttTelemetrySnapshot snapshot)
    {
        if (snapshot.HouseConsumptionWatts is null or 0m)
        {
            return snapshot.GridPowerMeasuredAtUtc;
        }

        return snapshot.HouseConsumptionMeasuredAtUtc;
    }

    private static int DecimalToInt(decimal value)
    {
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
