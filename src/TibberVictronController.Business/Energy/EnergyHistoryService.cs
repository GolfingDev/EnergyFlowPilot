using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Business.Energy;

public sealed record EnergyHistoryDependencies(IEnergyMeasurementReader Reader, IEnergyQuarterHourRepository Repository, IUtcClock Clock);

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

        var results = fromLog.Keys.Union(fromLive.Keys)
            .Select(slot => EnergyQuarterHourCalculator.Merge(existing.GetValueOrDefault(slot),
                EnergyQuarterHourCalculator.Combine(fromLive.GetValueOrDefault(slot), fromLog.GetValueOrDefault(slot))))
            .ToArray();
        if (results.Length > 0) await dependencies.Repository.SaveAsync(results, cancellationToken);
        return results.Length;
    }
}
