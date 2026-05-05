using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Provides future Tibber electricity prices for the Decision Engine forecast.
/// </summary>
public interface ITibberPriceForecastProvider
{
    /// <summary>
    /// Gets future electricity prices for the requested UTC time range.
    /// </summary>
    Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default);
}
