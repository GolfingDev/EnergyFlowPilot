using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Battery;
using TibberVictronController.Dal.Mqtt;

namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Uses live MQTT SoC when available and falls back to the configured backup value when MQTT is unavailable.
/// </summary>
public sealed class ResilientBatteryStateProvider : IBatteryStateProvider
{
    private readonly MqttBatteryStateProvider mqttBatteryStateProvider;
    private readonly ConfiguredBatteryStateProvider configuredBatteryStateProvider;
    private readonly VictronMqttRuntimeStatus runtimeStatus;

    public ResilientBatteryStateProvider(
        MqttBatteryStateProvider mqttBatteryStateProvider,
        ConfiguredBatteryStateProvider configuredBatteryStateProvider,
        VictronMqttRuntimeStatus runtimeStatus)
    {
        this.mqttBatteryStateProvider = mqttBatteryStateProvider;
        this.configuredBatteryStateProvider = configuredBatteryStateProvider;
        this.runtimeStatus = runtimeStatus;
    }

    public async Task<BatteryState> GetCurrentBatteryStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await mqttBatteryStateProvider.GetCurrentBatteryStateAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            runtimeStatus.MarkFailed("MQTT liefert aktuell keinen verwendbaren Live-SoC. Es wird auf den letzten konfigurierten SoC zurueckgegriffen.");
            return await configuredBatteryStateProvider.GetCurrentBatteryStateAsync(cancellationToken);
        }
    }
}
