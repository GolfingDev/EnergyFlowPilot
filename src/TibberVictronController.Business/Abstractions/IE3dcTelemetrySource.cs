using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>Read-only hardware boundary. Shadow planning cannot issue device commands.</summary>
public interface IE3dcTelemetrySource
{
    Task<E3dcLocalTelemetry> ReadAsync(CancellationToken cancellationToken = default);
}
