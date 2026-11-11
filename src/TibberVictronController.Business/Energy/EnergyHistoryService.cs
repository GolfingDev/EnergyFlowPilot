using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Business.Energy;

/// <param name="PriceArchive">Published prices; where it has the price that applied, it is used for the whole quarter hour.</param>
public sealed record EnergyHistoryDependencies(IEnergyMeasurementReader Reader, IEnergyQuarterHourRepository Repository, IUtcClock Clock,
    IPublishedPriceArchive? PriceArchive = null);

/// <summary>
/// Recalculates the stored quarter hours of a period from the raw measurements. Idempotent, and never replaces a more
/// complete stored value with a less complete one (raw data is deleted after a while, the stored history is kept).
/// </summary>
public sealed class EnergyHistoryService(EnergyHistoryDependencies dependencies)
{
    public async Task<int> RecalculateAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
    {
        var from = EnergyQuarterHourCalculator.SlotStart(fromUtc);
        var to = EnergyQuarterHourCalculator.SlotStart(toUtc);
        if (to <= from) return 0;
        var now = dependencies.Clock.UtcNow;
        // A little before and after the period, so the first and last values of the period know their neighbours.
        var logPoints = await dependencies.Reader.GetDecisionLogPointsAsync(from - EnergyQuarterHourCalculator.DecisionLogMaximumGap,
            to + EnergyQuarterHourCalculator.DecisionLogMaximumGap, cancellationToken);
        var livePoints = await dependencies.Reader.GetLivePointsAsync(from - EnergyQuarterHourCalculator.LiveMaximumGap,
            to + EnergyQuarterHourCalculator.LiveMaximumGap, cancellationToken);
        var fromLog = EnergyQuarterHourCalculator.Calculate(logPoints, EnergySampleSource.DecisionLog,
            EnergyQuarterHourCalculator.DecisionLogMaximumGap, from, to, now).ToDictionary(slot => slot.SlotStartUtc);
        var fromLive = EnergyQuarterHourCalculator.Calculate(livePoints, EnergySampleSource.LiveSamples,
            EnergyQuarterHourCalculator.LiveMaximumGap, from, to, now).ToDictionary(slot => slot.SlotStartUtc);
        var existing = (await dependencies.Repository.GetAsync(from, to, cancellationToken)).ToDictionary(slot => slot.SlotStartUtc);
        // The decision log knows the price only while decisions are logged (not e.g. while the joint execution is
        // blocked); the archive has the published price of the whole quarter hour. Energy costs are in EUR.
        var applied = dependencies.PriceArchive is null ? new Dictionary<DateTimeOffset, decimal>()
            : (await dependencies.PriceArchive.GetAppliedAsync(from, to, "EUR", cancellationToken))
                .ToDictionary(price => price.TimeSlot.StartsAtUtc, price => price.TotalPricePerKwh);

        var results = fromLog.Keys.Union(fromLive.Keys)
            .Select(slot => EnergyQuarterHourCalculator.Merge(existing.GetValueOrDefault(slot), WithAppliedPrice(
                EnergyQuarterHourCalculator.Combine(fromLive.GetValueOrDefault(slot), fromLog.GetValueOrDefault(slot)), applied)))
            .ToArray();
        if (results.Length > 0) await dependencies.Repository.SaveAsync(results, cancellationToken);
        return results.Length;
    }

    private static EnergyQuarterHour WithAppliedPrice(EnergyQuarterHour slot, IReadOnlyDictionary<DateTimeOffset, decimal> applied) =>
        applied.TryGetValue(slot.SlotStartUtc, out var price)
            ? slot with { PricePerKwh = price, PriceCoveredSeconds = EnergyQuarterHour.SlotSeconds }
            : slot;
}
