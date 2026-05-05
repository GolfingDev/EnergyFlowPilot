using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Battery;
using TibberVictronController.Dal.Mqtt;

namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Uses live MQTT site telemetry and fails loudly when no usable live value is available.
/// </summary>
public sealed class ResilientCurrentSiteTelemetryProvider : ICurrentSiteTelemetryProvider
{
    private readonly MqttCurrentSiteTelemetryProvider mqttCurrentSiteTelemetryProvider;
    private readonly VictronMqttRuntimeStatus runtimeStatus;

    public ResilientCurrentSiteTelemetryProvider(
        MqttCurrentSiteTelemetryProvider mqttCurrentSiteTelemetryProvider,
        VictronMqttRuntimeStatus runtimeStatus)
    {
        this.mqttCurrentSiteTelemetryProvider = mqttCurrentSiteTelemetryProvider;
        this.runtimeStatus = runtimeStatus;
    }

    public async Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await mqttCurrentSiteTelemetryProvider.GetCurrentSiteTelemetryAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            runtimeStatus.MarkFailed("MQTT liefert aktuell keine verwendbare Live-Telemetrie. Es werden keine Ersatzwerte verwendet.");
            throw new InvalidOperationException(
                "MQTT liefert aktuell keine verwendbare Live-Telemetrie. Es werden keine Ersatzwerte verwendet.",
                exception);
        }
    }
}
