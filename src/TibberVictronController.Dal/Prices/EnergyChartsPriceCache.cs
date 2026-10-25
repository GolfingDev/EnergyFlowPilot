using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Dal.Prices;

/// <summary>Raw market prices only: a tariff edit must never reuse previously calculated retail prices.</summary>
public sealed class EnergyChartsPriceCache
{
    internal readonly SemaphoreSlim Gate = new(1, 1);
    internal readonly Dictionary<string, (DateTimeOffset Expires, IReadOnlyList<EnergyMarketPrice> Prices)> Entries = [];
    internal readonly Dictionary<string, DateTimeOffset> RetryAfter = [];
}
