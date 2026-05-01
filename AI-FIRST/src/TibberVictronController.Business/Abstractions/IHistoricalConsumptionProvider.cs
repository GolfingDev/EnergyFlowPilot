using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Provides consumption estimates derived from historical household consumption data.
/// </summary>
public interface IHistoricalConsumptionProvider
{
    /// <summary>
    /// Gets expected consumption for the requested UTC time range.
    /// </summary>
    Task<IReadOnlyList<ConsumptionForecastSlot>> GetConsumptionForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default);
}
