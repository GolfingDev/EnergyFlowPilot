namespace TibberVictronController.Business.Models;

/// <summary>
/// Outdoor temperature for one hour, measured or forecast.
/// </summary>
public sealed record HourlyTemperature(DateTimeOffset StartsAtUtc, decimal TemperatureCelsius);
