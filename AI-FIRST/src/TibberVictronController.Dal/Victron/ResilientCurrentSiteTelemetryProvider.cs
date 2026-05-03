using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Battery;
using TibberVictronController.Dal.Mqtt;

namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Uses live MQTT site telemetry when available and falls back to configured backup values when MQTT is unavailable.
/// </summary>
public sealed class ResilientCurrentSiteTelemetryProvider : ICurrentSiteTelemetryProvider
{
    private readonly MqttCurrentSiteTelemetryProvider mqttCurrentSiteTelemetryProvider;
    private readonly ConfiguredCurrentSiteTelemetryProvider configuredCurrentSiteTelemetryProvider;
    private readonly VictronMqttRuntimeStatus runtimeStatus;

    public ResilientCurrentSiteTelemetryProvider(
        MqttCurrentSiteTelemetryProvider mqttCurrentSiteTelemetryProvider,
        ConfiguredCurrentSiteTelemetryProvider configuredCurrentSiteTelemetryProvider,
        VictronMqttRuntimeStatus runtimeStatus)
    {
        this.mqttCurrentSiteTelemetryProvider = mqttCurrentSiteTelemetryProvider;
        this.configuredCurrentSiteTelemetryProvider = configuredCurrentSiteTelemetryProvider;
        this.runtimeStatus = runtimeStatus;
    }

    public async Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await mqttCurrentSiteTelemetryProvider.GetCurrentSiteTelemetryAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            runtimeStatus.MarkFailed("MQTT liefert aktuell keine verwendbare Live-Telemetrie. Es werden konfigurierte Ersatzwerte verwendet.");
            return await configuredCurrentSiteTelemetryProvider.GetCurrentSiteTelemetryAsync(cancellationToken);
        }
    }
}
