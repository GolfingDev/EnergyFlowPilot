using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Weather;

/// <summary>
/// Adds a one-hour cache around Forecast.Solar calls while keeping external API failures visible to callers.
/// </summary>
public sealed class CachedWeatherForecastProvider : IWeatherForecastProvider
{
    private readonly ForecastSolarPvForecastProvider innerProvider;
    private readonly ForecastSolarPvForecastCache forecastCache;

    public CachedWeatherForecastProvider(
        ForecastSolarPvForecastProvider innerProvider,
        ForecastSolarPvForecastCache forecastCache)
    {
        this.innerProvider = innerProvider;
        this.forecastCache = forecastCache;
    }

    public Task<IReadOnlyList<PvYieldForecastSlot>> GetPvYieldForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{startsAtUtc:O}|{endsAtUtc:O}";

        return forecastCache.GetOrCreateAsync(
            cacheKey,
            token => innerProvider.GetPvYieldForecastAsync(startsAtUtc, endsAtUtc, token),
            cancellationToken);
    }
}
