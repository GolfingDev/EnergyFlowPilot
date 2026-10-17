using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Orchestrates forecast inputs and calculates the Decision Engine forecast.
/// </summary>
public interface IBatteryForecastService
{
    /// <summary>
    /// Calculates a Decision Engine forecast for the requested UTC time range.
    /// </summary>
    Task<BatteryForecastResult> CalculateForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Calculates a Decision Engine forecast; implementations that do not support the live load ignore it.
    /// </summary>
    Task<BatteryForecastResult> CalculateForecastAsync(
        BatteryForecastRequest request,
        CancellationToken cancellationToken = default)
    {
        return CalculateForecastAsync(request.StartsAtUtc, request.EndsAtUtc, cancellationToken);
    }
}
