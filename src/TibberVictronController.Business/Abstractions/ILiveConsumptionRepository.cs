using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Persists real measured live energy samples for later analysis and forecasting.
/// </summary>
public interface ILiveConsumptionRepository
{
    /// <summary>
    /// Saves one live energy sample.
    /// </summary>
    Task SaveSampleAsync(LiveConsumptionSample sample, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads live energy samples in the given UTC range ordered by measurement time.
    /// </summary>
    Task<IReadOnlyList<LiveConsumptionSample>> GetSamplesAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes raw live samples older than the given UTC threshold.
    /// </summary>
    Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default);
}
