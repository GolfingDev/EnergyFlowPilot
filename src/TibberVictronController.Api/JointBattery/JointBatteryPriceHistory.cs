using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

/// <summary>Caches completed historical days across the 30-second planning cycles.</summary>
public sealed class JointBatteryPriceHistory
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private HistoricalPriceRequest? cachedRequest;
    private IReadOnlyList<TibberPriceForecastSlot> cachedPrices = [];

    public async Task<IReadOnlyList<TibberPriceForecastSlot>> GetAsync(IDecisionLogRepository repository,
        HistoricalPriceRequest request, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (request != cachedRequest)
            {
                var prices = await repository.GetHistoricalPricesAsync(request, token);
                cachedPrices = prices;
                cachedRequest = request;
            }
            return cachedPrices;
        }
        finally { gate.Release(); }
    }
}
