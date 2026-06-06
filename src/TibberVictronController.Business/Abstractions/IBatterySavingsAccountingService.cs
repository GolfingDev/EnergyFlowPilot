using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Rebuilds persisted battery savings summaries from measured live telemetry.
/// </summary>
public interface IBatterySavingsAccountingService
{
    Task RefreshAsync(BatterySavingsQuery query, CancellationToken cancellationToken = default);

    Task RefreshRecentDaysAsync(int dayCount, string currency, CancellationToken cancellationToken = default);
}
