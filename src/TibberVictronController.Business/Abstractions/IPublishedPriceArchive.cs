using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Durable record of the retail prices a supplier published, per quarter hour. Until 2026-11-11 the decision log was
/// the only price history; it has gaps whenever no decision is logged (e.g. joint execution blocked) and must not
/// grow forever. Estimated prices are never stored here.
/// </summary>
public interface IPublishedPriceArchive
{
    /// <summary>Stores published slots of <paramref name="source"/>; a later recording of the same slot replaces the earlier one.</summary>
    Task SaveAsync(string source, IReadOnlyList<TibberPriceForecastSlot> publishedSlots, DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Archived quarter hours of the requested supplier and currency starting in the UTC range, ordered by start.</summary>
    Task<IReadOnlyList<TibberPriceForecastSlot>> GetAsync(HistoricalPriceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The price that applied per quarter hour, whichever supplier it came from. A quarter hour archived for several
    /// suppliers is left out (it cannot be decided which one applied).
    /// </summary>
    Task<IReadOnlyList<TibberPriceForecastSlot>> GetAppliedAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, string currency,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes prices of other suppliers for quarter hours from <paramref name="fromUtc"/> on: they were recorded in
    /// advance (day-ahead) before a change of supplier and never applied.
    /// </summary>
    Task RemoveOtherSuppliersFromAsync(string source, DateTimeOffset fromUtc, CancellationToken cancellationToken = default);
}
