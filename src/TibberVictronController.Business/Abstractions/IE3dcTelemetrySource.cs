using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>Read-only hardware boundary: the joint planning reads the E3/DC through it and cannot issue device commands.</summary>
public interface IE3dcTelemetrySource
{
    Task<E3dcLocalTelemetry> ReadAsync(CancellationToken cancellationToken = default);
}
