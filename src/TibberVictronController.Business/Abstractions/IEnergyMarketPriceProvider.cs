using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

public sealed record EnergyMarketPriceRequest(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string BiddingZone);
public sealed record EnergyMarketPrice(ForecastTimeSlot TimeSlot, decimal NetPricePerKwh);

public interface IEnergyMarketPriceProvider
{
    Task<IReadOnlyList<EnergyMarketPrice>> GetMarketPricesAsync(EnergyMarketPriceRequest request, CancellationToken token = default);
}
