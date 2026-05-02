using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Battery;

namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Uses live Victron site telemetry when available and falls back to configured backup values when MQTT is unavailable.
/// </summary>
public sealed class ResilientCurrentSiteTelemetryProvider : ICurrentSiteTelemetryProvider
{
    private readonly VictronCurrentSiteTelemetryProvider victronCurrentSiteTelemetryProvider;
    private readonly ConfiguredCurrentSiteTelemetryProvider configuredCurrentSiteTelemetryProvider;
    private readonly VictronMqttRuntimeStatus runtimeStatus;

    public ResilientCurrentSiteTelemetryProvider(
        VictronCurrentSiteTelemetryProvider victronCurrentSiteTelemetryProvider,
        ConfiguredCurrentSiteTelemetryProvider configuredCurrentSiteTelemetryProvider,
        VictronMqttRuntimeStatus runtimeStatus)
    {
        this.victronCurrentSiteTelemetryProvider = victronCurrentSiteTelemetryProvider;
        this.configuredCurrentSiteTelemetryProvider = configuredCurrentSiteTelemetryProvider;
        this.runtimeStatus = runtimeStatus;
    }

    public async Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await victronCurrentSiteTelemetryProvider.GetCurrentSiteTelemetryAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            runtimeStatus.MarkFailed("Victron MQTT liefert aktuell keine verwendbare Live-Telemetrie. Es werden konfigurierte Ersatzwerte verwendet.");
            return await configuredCurrentSiteTelemetryProvider.GetCurrentSiteTelemetryAsync(cancellationToken);
        }
    }
}
