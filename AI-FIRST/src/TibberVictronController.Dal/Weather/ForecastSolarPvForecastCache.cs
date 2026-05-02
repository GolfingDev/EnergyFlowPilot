using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Weather;

/// <summary>
/// Caches Forecast.Solar responses for a short period so dashboard refreshes do not hit the external API repeatedly.
/// </summary>
public sealed class ForecastSolarPvForecastCache
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim cacheLock = new(1, 1);
    private readonly Dictionary<string, ForecastSolarPvForecastCacheEntry> entries = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<PvYieldForecastSlot>> GetOrCreateAsync(
        string cacheKey,
        Func<CancellationToken, Task<IReadOnlyList<PvYieldForecastSlot>>> loadForecastAsync,
        CancellationToken cancellationToken)
    {
        await cacheLock.WaitAsync(cancellationToken);

        try
        {
            var nowUtc = DateTimeOffset.UtcNow;

            if (entries.TryGetValue(cacheKey, out var cacheEntry) &&
                nowUtc - cacheEntry.CreatedAtUtc < CacheDuration)
            {
                return cacheEntry.GetForecastOrThrow();
            }

            try
            {
                var forecastSlots = await loadForecastAsync(cancellationToken);
                entries[cacheKey] = ForecastSolarPvForecastCacheEntry.FromForecast(nowUtc, forecastSlots);

                return forecastSlots;
            }
            catch (ForecastSolarApiException exception)
            {
                entries[cacheKey] = ForecastSolarPvForecastCacheEntry.FromError(nowUtc, exception.Message);

                throw;
            }
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private sealed class ForecastSolarPvForecastCacheEntry
    {
        private ForecastSolarPvForecastCacheEntry(
            DateTimeOffset createdAtUtc,
            IReadOnlyList<PvYieldForecastSlot>? forecastSlots,
            string? errorMessage)
        {
            CreatedAtUtc = createdAtUtc;
            ForecastSlots = forecastSlots;
            ErrorMessage = errorMessage;
        }

        public DateTimeOffset CreatedAtUtc { get; }

        private IReadOnlyList<PvYieldForecastSlot>? ForecastSlots { get; }

        private string? ErrorMessage { get; }

        public static ForecastSolarPvForecastCacheEntry FromForecast(
            DateTimeOffset createdAtUtc,
            IReadOnlyList<PvYieldForecastSlot> forecastSlots)
        {
            return new ForecastSolarPvForecastCacheEntry(createdAtUtc, forecastSlots, null);
        }

        public static ForecastSolarPvForecastCacheEntry FromError(
            DateTimeOffset createdAtUtc,
            string errorMessage)
        {
            return new ForecastSolarPvForecastCacheEntry(createdAtUtc, null, errorMessage);
        }

        public IReadOnlyList<PvYieldForecastSlot> GetForecastOrThrow()
        {
            if (ForecastSlots is not null)
            {
                return ForecastSlots;
            }

            throw new ForecastSolarApiException($"Forecast.Solar hat beim letzten Abruf einen Fehler geliefert. Der Fehler wird fuer maximal eine Stunde zwischengespeichert: {ErrorMessage}");
        }
    }
}
