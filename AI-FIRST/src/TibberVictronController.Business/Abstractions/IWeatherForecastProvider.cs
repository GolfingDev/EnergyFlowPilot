using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Provides weather-derived PV yield estimates for the Decision Engine forecast.
/// </summary>
public interface IWeatherForecastProvider
{
    /// <summary>
    /// Gets expected PV yield for the requested UTC time range.
    /// </summary>
    Task<IReadOnlyList<PvYieldForecastSlot>> GetPvYieldForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default);
}
