using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Provides the latest live site telemetry required before a direct control decision can be made.
/// </summary>
public interface ICurrentSiteTelemetryProvider
{
    /// <summary>
    /// Gets the latest live site telemetry snapshot.
    /// </summary>
    Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default);
}
