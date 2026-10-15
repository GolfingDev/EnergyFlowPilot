using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Builds the control-room dashboard view from decisions, measured samples and the forecast plan.
/// </summary>
public interface IDashboardSummaryService
{
    /// <summary>
    /// Returns current state, measured values of both storage systems, live checks and the plan for the next hours.
    /// </summary>
    Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the measured review for the given UTC period, including feed-in and battery-to-battery transfer.
    /// </summary>
    Task<DashboardReview> GetReviewAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default);
}
