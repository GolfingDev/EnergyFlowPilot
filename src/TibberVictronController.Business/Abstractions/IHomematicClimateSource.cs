using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Reads current room climate values from the Homematic CCU. Implementations must be strictly read-only.
/// </summary>
public interface IHomematicClimateSource
{
    /// <summary>
    /// Reads all supported climate channels and stamps them with the given UTC polling time.
    /// </summary>
    Task<HomematicClimateSnapshot> GetCurrentSnapshotAsync(DateTimeOffset polledAtUtc, CancellationToken cancellationToken = default);
}
