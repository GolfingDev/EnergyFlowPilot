using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Persists realtime Decision Engine decisions with structured reasons.
/// </summary>
public interface IDecisionLogRepository
{
    /// <summary>
    /// Saves one realtime Decision Engine decision.
    /// </summary>
    Task SaveDecisionAsync(DecisionLogEntry decisionLogEntry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent realtime decisions ordered from newest to oldest.
    /// </summary>
    Task<IReadOnlyList<DecisionLogEntry>> GetRecentDecisionsAsync(int maxCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets realtime decisions in the UTC time range ordered from oldest to newest.
    /// </summary>
    Task<IReadOnlyList<DecisionLogEntry>> GetDecisionsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes decision logs older than the configured UTC cutoff.
    /// </summary>
    Task<int> DeleteDecisionsOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default);
}
