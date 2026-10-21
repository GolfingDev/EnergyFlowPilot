using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Provides future Tibber electricity prices for the Decision Engine forecast.
/// </summary>
public interface ITibberPriceForecastProvider : IElectricityPriceForecastProvider
{
    // Compatibility alias for existing Tibber adapters and test doubles.
}
