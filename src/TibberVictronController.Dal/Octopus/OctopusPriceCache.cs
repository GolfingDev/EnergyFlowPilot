using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Octopus;

/// <summary>Process-local state, serialized across scoped consumers. Keys are fingerprints, never raw credentials.</summary>
public sealed class OctopusPriceCache
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal string? Key { get; set; }
    internal string? Token { get; set; }
    internal DateTimeOffset TokenExpiresAtUtc { get; set; }
    internal DateTimeOffset PricesExpireAtUtc { get; set; }
    internal DateTimeOffset RetryAfterUtc { get; set; }
    internal IReadOnlyList<TibberPriceForecastSlot> Prices { get; set; } = [];
}
