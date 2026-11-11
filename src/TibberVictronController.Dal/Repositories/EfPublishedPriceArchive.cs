using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfPublishedPriceArchive(ControllerDbContext dbContext) : IPublishedPriceArchive
{
    public const string ProviderOrigin = "provider";
    public const string DecisionLogOrigin = "decisionLog";

    public async Task SaveAsync(string source, IReadOnlyList<TibberPriceForecastSlot> publishedSlots, DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Die Preisquelle muss angegeben werden.", nameof(source));
        var slots = publishedSlots.Where(slot => !slot.IsEstimated)
            .GroupBy(slot => (slot.Currency, slot.TimeSlot.StartsAtUtc)).Select(group => group.Last()).ToArray();
        if (slots.Length == 0) return;
        var from = slots.Min(slot => slot.TimeSlot.StartsAtUtc);
        var to = slots.Max(slot => slot.TimeSlot.StartsAtUtc);
        var existing = (await dbContext.PublishedPriceSlots
                .Where(row => row.Source == source && row.SlotStartUtc >= from && row.SlotStartUtc <= to)
                .ToListAsync(cancellationToken))
            .ToDictionary(row => (row.Currency, row.SlotStartUtc));
        foreach (var slot in slots)
        {
            if (!existing.TryGetValue((slot.Currency, slot.TimeSlot.StartsAtUtc), out var row))
            {
                row = new PublishedPriceSlotEntity { Source = source, Currency = slot.Currency, SlotStartUtc = slot.TimeSlot.StartsAtUtc };
                dbContext.PublishedPriceSlots.Add(row);
            }
            // The supplier's latest publication counts, also over a value imported from the decision log.
            row.SlotEndUtc = slot.TimeSlot.EndsAtUtc;
            row.PricePerKwh = slot.TotalPricePerKwh;
            row.RecordedAtUtc = recordedAtUtc;
            row.Origin = ProviderOrigin;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TibberPriceForecastSlot>> GetAsync(HistoricalPriceRequest request, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.PublishedPriceSlots.AsNoTracking()
            .Where(row => row.Source == request.Source && row.Currency == request.Currency &&
                row.SlotStartUtc >= request.FromUtc && row.SlotStartUtc < request.ToUtc)
            .OrderBy(row => row.SlotStartUtc)
            .ToListAsync(cancellationToken);
        return rows.Select(row => new TibberPriceForecastSlot(new ForecastTimeSlot(row.SlotStartUtc, row.SlotEndUtc), row.PricePerKwh, row.Currency))
            .ToArray();
    }

    /// <summary>Time of the oldest decision log entry, the start of the price import.</summary>
    public async Task<DateTimeOffset?> GetOldestDecisionAtUtcAsync(CancellationToken cancellationToken = default) =>
        await dbContext.DecisionLogEntries.AsNoTracking().OrderBy(entry => entry.DecidedAtUtc)
            .Select(entry => (DateTimeOffset?)entry.DecidedAtUtc).FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Imports the prices logged with decisions in the UTC range, aggregated per quarter hour, supplier and currency, by
    /// the same rules as <see cref="EfDecisionLogRepository.GetHistoricalPricesAsync"/>: estimated prices are skipped,
    /// untagged single-battery logs belong to Tibber, untagged joint logs to no supplier. Existing rows win, so the import
    /// can be repeated and never overrides a price recorded from the supplier.
    /// </summary>
    /// <returns>Number of imported quarter hours.</returns>
    public Task<int> ImportFromDecisionLogAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero || toUtc <= fromUtc)
            throw new ArgumentException("Der Importzeitraum muss ein gueltiger UTC-Zeitraum sein.");
        return dbContext.Database.ExecuteSqlRawAsync("""
            INSERT OR IGNORE INTO "PublishedPriceSlots" ("Source", "Currency", "SlotStartUtc", "SlotEndUtc", "PricePerKwh", "RecordedAtUtc", "Origin")
            SELECT Source, Currency, SlotStart, SlotStart + 900000, AVG(Price), {2}, 'decisionLog'
            FROM (
                SELECT (DecidedAtUtc / 900000) * 900000 AS SlotStart, CAST(TibberPricePerKwh AS REAL) AS Price, TibberPriceCurrency AS Currency,
                       CASE WHEN json_valid(InputSummaryJson) AND COALESCE(json_extract(InputSummaryJson, '$.IsPriceEstimated'), 0) = 0
                           THEN COALESCE(json_extract(InputSummaryJson, '$.ElectricityPriceProvider'),
                               CASE WHEN json_extract(InputSummaryJson, '$.ControlSource') IS NULL THEN 'tibber' ELSE NULL END)
                           ELSE NULL END AS Source
                FROM DecisionLogEntries
                WHERE DecidedAtUtc >= {0} AND DecidedAtUtc < {1} AND TibberPricePerKwh IS NOT NULL AND TibberPriceCurrency IS NOT NULL)
            WHERE Source IS NOT NULL
            GROUP BY Source, Currency, SlotStart;
            """, [fromUtc.ToUnixTimeMilliseconds(), toUtc.ToUnixTimeMilliseconds(), recordedAtUtc.ToUnixTimeMilliseconds()], cancellationToken);
    }
}
