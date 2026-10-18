namespace TibberVictronController.Business.Models;

/// <summary>
/// Weather that moves the German day-ahead price for one hour.
/// </summary>
/// <param name="WindIndex">Mean relative output (0 to 1) of a simple turbine curve over northern German locations.</param>
/// <param name="SolarKwPerSquareMeter">Mean global irradiance over southern and central German locations.</param>
public sealed record PriceDriverHour(DateTimeOffset StartsAtUtc, decimal WindIndex, decimal SolarKwPerSquareMeter);

/// <summary>
/// German day-ahead (spot) price of one market slot.
/// </summary>
public sealed record SpotPriceSlot(DateTimeOffset StartsAtUtc, decimal PricePerMwh);

/// <summary>
/// Inputs for the weather-based price estimate, loaded together so the fitted model can be cached per load.
/// </summary>
public sealed record PriceDriverData(
    IReadOnlyList<PriceDriverHour> Weather,
    IReadOnlyList<SpotPriceSlot> SpotPrices,
    DateTimeOffset LoadedAtUtc);
