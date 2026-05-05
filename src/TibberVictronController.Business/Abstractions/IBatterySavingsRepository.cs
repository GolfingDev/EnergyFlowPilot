using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Persists and aggregates daily monetary battery savings summaries.
/// </summary>
public interface IBatterySavingsRepository
{
    Task SaveDailySummaryAsync(
        BatterySavingsDailySummary summary,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BatterySavingsDailySummary>> GetDailySummariesAsync(
        BatterySavingsQuery query,
        CancellationToken cancellationToken = default);

    Task<BatterySavingsAggregate> GetAggregateAsync(
        BatterySavingsQuery query,
        CancellationToken cancellationToken = default);
}
