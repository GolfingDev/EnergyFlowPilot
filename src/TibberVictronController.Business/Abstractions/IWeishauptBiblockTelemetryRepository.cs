using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

public interface IWeishauptBiblockTelemetryRepository
{
    Task SaveSampleAsync(WeishauptBiblockTelemetrySample sample, CancellationToken cancellationToken = default);

    Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WeishauptBiblockTelemetrySample>> GetSamplesAsync(
        DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default);
}
