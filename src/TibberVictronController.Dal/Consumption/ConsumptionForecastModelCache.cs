using TibberVictronController.Business.Decisions;

namespace TibberVictronController.Dal.Consumption;

/// <summary>
/// Keeps the learned consumption model across scoped requests so the sample history is only reloaded once per hour.
/// </summary>
public sealed class ConsumptionForecastModelCache
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim cacheLock = new(1, 1);
    private ConsumptionForecastModel? model;
    private string? timeZoneId;
    private DateTimeOffset builtForSampleUtc;

    public async Task<ConsumptionForecastModel> GetOrCreateAsync(
        string currentTimeZoneId,
        DateTimeOffset latestSampleUtc,
        Func<CancellationToken, Task<ConsumptionForecastModel>> buildAsync,
        CancellationToken cancellationToken)
    {
        await cacheLock.WaitAsync(cancellationToken);

        try
        {
            // Samples arrive every few seconds, so rebuilding on every newer sample would reload the whole history on each decision cycle.
            if (model is null ||
                !string.Equals(timeZoneId, currentTimeZoneId, StringComparison.Ordinal) ||
                latestSampleUtc - builtForSampleUtc >= RefreshInterval)
            {
                model = await buildAsync(cancellationToken);
                timeZoneId = currentTimeZoneId;
                builtForSampleUtc = latestSampleUtc;
            }

            return model;
        }
        finally
        {
            cacheLock.Release();
        }
    }
}
