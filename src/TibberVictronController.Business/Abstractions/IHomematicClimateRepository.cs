using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Persists Homematic climate samples for later heating curve analysis.
/// </summary>
public interface IHomematicClimateRepository
{
    /// <summary>
    /// Saves all samples of one polling cycle.
    /// </summary>
    Task SaveSamplesAsync(IReadOnlyList<HomematicClimateSample> samples, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads samples in the given UTC range ordered by polling time.
    /// </summary>
    Task<IReadOnlyList<HomematicClimateSample>> GetSamplesAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes samples polled before the given UTC threshold.
    /// </summary>
    Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default);
}
