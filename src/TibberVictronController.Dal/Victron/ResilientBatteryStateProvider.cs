using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Battery;
using TibberVictronController.Dal.Mqtt;

namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Uses live MQTT SoC and fails loudly when no usable live value is available.
/// </summary>
public sealed class ResilientBatteryStateProvider : IBatteryStateProvider
{
    private readonly MqttBatteryStateProvider mqttBatteryStateProvider;
    private readonly VictronMqttRuntimeStatus runtimeStatus;

    public ResilientBatteryStateProvider(
        MqttBatteryStateProvider mqttBatteryStateProvider,
        VictronMqttRuntimeStatus runtimeStatus)
    {
        this.mqttBatteryStateProvider = mqttBatteryStateProvider;
        this.runtimeStatus = runtimeStatus;
    }

    public async Task<BatteryState> GetCurrentBatteryStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await mqttBatteryStateProvider.GetCurrentBatteryStateAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            runtimeStatus.MarkFailed("MQTT liefert aktuell keinen verwendbaren Live-SoC. Es wird kein Ersatzwert verwendet.");
            throw new InvalidOperationException(
                "MQTT liefert aktuell keinen verwendbaren Live-SoC. Es wird kein Ersatzwert verwendet.",
                exception);
        }
    }
}
