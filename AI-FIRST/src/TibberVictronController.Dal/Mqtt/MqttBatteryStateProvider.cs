using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Mqtt;

/// <summary>
/// Provides the current battery state from the latest MQTT snapshot.
/// </summary>
public sealed class MqttBatteryStateProvider : IBatteryStateProvider
{
    private readonly MqttTelemetrySnapshotStore snapshotStore;

    public MqttBatteryStateProvider(MqttTelemetrySnapshotStore snapshotStore)
    {
        this.snapshotStore = snapshotStore;
    }

    public Task<BatteryState> GetCurrentBatteryStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = snapshotStore.GetSnapshot();

        if (snapshot.BatterySocPercent is null || snapshot.BatterySocMeasuredAtUtc is null)
        {
            throw new InvalidOperationException("Es liegt noch kein Live-Akkuladestand aus MQTT vor.");
        }

        return Task.FromResult(new BatteryState(
            snapshot.BatterySocPercent.Value,
            snapshot.BatterySocMeasuredAtUtc.Value));
    }
}
