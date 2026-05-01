using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Persists technical and operational events for diagnostics.
/// </summary>
public interface IOperationalEventRepository
{
    /// <summary>
    /// Saves one operational event.
    /// </summary>
    Task SaveEventAsync(OperationalEvent operationalEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent operational events ordered from newest to oldest.
    /// </summary>
    Task<IReadOnlyList<OperationalEvent>> GetRecentEventsAsync(int maxCount, CancellationToken cancellationToken = default);
}
