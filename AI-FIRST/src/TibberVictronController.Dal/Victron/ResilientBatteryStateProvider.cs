using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Battery;

namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Uses live Victron SoC when available and falls back to the configured backup value when MQTT is unavailable.
/// </summary>
public sealed class ResilientBatteryStateProvider : IBatteryStateProvider
{
    private readonly VictronBatteryStateProvider victronBatteryStateProvider;
    private readonly ConfiguredBatteryStateProvider configuredBatteryStateProvider;
    private readonly VictronMqttRuntimeStatus runtimeStatus;

    public ResilientBatteryStateProvider(
        VictronBatteryStateProvider victronBatteryStateProvider,
        ConfiguredBatteryStateProvider configuredBatteryStateProvider,
        VictronMqttRuntimeStatus runtimeStatus)
    {
        this.victronBatteryStateProvider = victronBatteryStateProvider;
        this.configuredBatteryStateProvider = configuredBatteryStateProvider;
        this.runtimeStatus = runtimeStatus;
    }

    public async Task<BatteryState> GetCurrentBatteryStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await victronBatteryStateProvider.GetCurrentBatteryStateAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            runtimeStatus.MarkFailed("Victron MQTT liefert aktuell keinen verwendbaren Live-SoC. Es wird auf den letzten konfigurierten SoC zurueckgegriffen.");
            return await configuredBatteryStateProvider.GetCurrentBatteryStateAsync(cancellationToken);
        }
    }
}
