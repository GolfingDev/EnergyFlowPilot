using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Tibber;

/// <summary>
/// Caches Tibber price forecasts and keeps the last good response usable during temporary API failures.
/// </summary>
public sealed class TibberPriceForecastCache
{
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim cacheLock = new(1, 1);
    private readonly Dictionary<string, TibberPriceForecastCacheEntry> entries = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<TibberPriceForecastSlot>> GetOrCreateAsync(
        string cacheKey,
        DateTimeOffset nowUtc,
        TimeSpan cacheDuration,
        Func<CancellationToken, Task<IReadOnlyList<TibberPriceForecastSlot>>> loadForecastAsync,
        CancellationToken cancellationToken)
    {
        await cacheLock.WaitAsync(cancellationToken);

        try
        {
            if (entries.TryGetValue(cacheKey, out var cacheEntry))
            {
                if (cacheEntry.ForecastSlots is not null &&
                    nowUtc - cacheEntry.CreatedAtUtc < cacheDuration)
                {
                    return cacheEntry.ForecastSlots;
                }

                if (cacheEntry.ForecastSlots is null &&
                    cacheEntry.ErrorMessage is not null &&
                    nowUtc - cacheEntry.CreatedAtUtc < ErrorRetryDelay)
                {
                    throw new TibberApiException(cacheEntry.ErrorMessage);
                }
            }

            try
            {
                var forecastSlots = await loadForecastAsync(cancellationToken);
                entries[cacheKey] = TibberPriceForecastCacheEntry.FromForecast(nowUtc, forecastSlots);
                return forecastSlots;
            }
            catch (Exception) when (cacheEntry?.ForecastSlots is not null)
            {
                return cacheEntry.ForecastSlots;
            }
            catch (Exception exception)
            {
                entries[cacheKey] = TibberPriceForecastCacheEntry.FromError(nowUtc, exception.Message);
                throw;
            }
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private sealed class TibberPriceForecastCacheEntry
    {
        private TibberPriceForecastCacheEntry(
            DateTimeOffset createdAtUtc,
            IReadOnlyList<TibberPriceForecastSlot>? forecastSlots,
            string? errorMessage)
        {
            CreatedAtUtc = createdAtUtc;
            ForecastSlots = forecastSlots;
            ErrorMessage = errorMessage;
        }

        public DateTimeOffset CreatedAtUtc { get; }

        public IReadOnlyList<TibberPriceForecastSlot>? ForecastSlots { get; }

        public string? ErrorMessage { get; }

        public static TibberPriceForecastCacheEntry FromForecast(
            DateTimeOffset createdAtUtc,
            IReadOnlyList<TibberPriceForecastSlot> forecastSlots)
        {
            return new TibberPriceForecastCacheEntry(createdAtUtc, forecastSlots, null);
        }

        public static TibberPriceForecastCacheEntry FromError(
            DateTimeOffset createdAtUtc,
            string errorMessage)
        {
            return new TibberPriceForecastCacheEntry(createdAtUtc, null, errorMessage);
        }
    }
}
