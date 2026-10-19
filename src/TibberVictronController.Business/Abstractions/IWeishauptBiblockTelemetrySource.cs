using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>Reads heat-pump telemetry without exposing any write operation.</summary>
public interface IWeishauptBiblockTelemetrySource
{
    Task<WeishauptBiblockTelemetrySample> GetCurrentSampleAsync(DateTimeOffset measuredAtUtc, CancellationToken cancellationToken = default);
}
