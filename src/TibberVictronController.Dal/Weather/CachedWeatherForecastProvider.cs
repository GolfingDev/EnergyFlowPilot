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

    private static readonly TimeSpan CanonicalWindowLength = TimeSpan.FromDays(3);

    public async Task<IReadOnlyList<PvYieldForecastSlot>> GetPvYieldForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        // Decision cycles request a window that moves every 15 minutes. Caching one fixed window per UTC day keeps the
        // number of Forecast.Solar calls at one per roof plane and hour, well below the public rate limit.
        var canonicalStartsAtUtc = new DateTimeOffset(startsAtUtc.UtcDateTime.Date, TimeSpan.Zero);
        var canonicalEndsAtUtc = canonicalStartsAtUtc.Add(CanonicalWindowLength);

        var slotTicks = TimeSpan.FromMinutes(15).Ticks;
        if (endsAtUtc > canonicalEndsAtUtc ||
            startsAtUtc.UtcTicks % slotTicks != 0 ||
            endsAtUtc.UtcTicks % slotTicks != 0)
        {
            return await innerProvider.GetPvYieldForecastAsync(startsAtUtc, endsAtUtc, cancellationToken);
        }

        var canonicalSlots = await forecastCache.GetOrCreateAsync(
            $"{canonicalStartsAtUtc:O}",
            token => innerProvider.GetPvYieldForecastAsync(canonicalStartsAtUtc, canonicalEndsAtUtc, token),
            cancellationToken);

        return canonicalSlots
            .Where(slot => slot.TimeSlot.StartsAtUtc >= startsAtUtc && slot.TimeSlot.EndsAtUtc <= endsAtUtc)
            .ToArray();
    }
}
