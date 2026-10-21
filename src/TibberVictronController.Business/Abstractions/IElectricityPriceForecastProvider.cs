using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>Selected supplier's published gross EUR/kWh prices. Legacy slot names remain wire-compatible.</summary>
public interface IElectricityPriceForecastProvider
{
    string Source => "tibber";

    Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(
        DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default);
}
