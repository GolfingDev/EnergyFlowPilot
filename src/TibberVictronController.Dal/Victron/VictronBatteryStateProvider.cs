using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Provides the current battery state from the latest Victron MQTT snapshot.
/// </summary>
public sealed class VictronBatteryStateProvider : IBatteryStateProvider
{
    private readonly VictronTelemetrySnapshotStore snapshotStore;

    public VictronBatteryStateProvider(VictronTelemetrySnapshotStore snapshotStore)
    {
        this.snapshotStore = snapshotStore;
    }

    public Task<BatteryState> GetCurrentBatteryStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = snapshotStore.GetSnapshot();

        if (snapshot.BatterySocPercent is null || snapshot.BatterySocMeasuredAtUtc is null)
        {
            throw new InvalidOperationException("Es liegt noch kein Live-Akkuladestand aus Victron MQTT vor.");
        }

        return Task.FromResult(new BatteryState(
            snapshot.BatterySocPercent.Value,
            snapshot.BatterySocMeasuredAtUtc.Value));
    }
}
