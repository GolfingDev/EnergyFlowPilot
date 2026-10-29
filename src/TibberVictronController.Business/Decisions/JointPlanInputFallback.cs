using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Operator rule (29.10.2026): a missing forecast or data source must never leave the current quarter hour without
/// an action. Missing inputs make the plan less precise; they never stop it. These helpers pick the best available
/// substitute and report what was substituted, so the dashboard can show it.
/// </summary>
public static class JointPlanInputFallback
{
    /// <summary>
    /// The longest gap-free run of prices starting exactly at <paramref name="startUtc"/>. A later gap shortens the
    /// horizon instead of discarding all prices (previously any gap stopped the plan).
    /// </summary>
    public static IReadOnlyList<TibberPriceForecastSlot> ContiguousFrom(IEnumerable<TibberPriceForecastSlot>? prices,
        DateTimeOffset startUtc, DateTimeOffset horizonEndUtc)
    {
        if (prices is null) return [];
        var ordered = prices
            .Where(p => p.TimeSlot.StartsAtUtc >= startUtc && p.TimeSlot.EndsAtUtc <= horizonEndUtc)
            .GroupBy(p => p.TimeSlot.StartsAtUtc).Select(g => g.First())
            .OrderBy(p => p.TimeSlot.StartsAtUtc).ToArray();
        if (ordered.Length == 0 || ordered[0].TimeSlot.StartsAtUtc != startUtc) return [];
        var run = new List<TibberPriceForecastSlot> { ordered[0] };
        foreach (var price in ordered.Skip(1))
        {
            if (price.TimeSlot.StartsAtUtc != run[^1].TimeSlot.EndsAtUtc) break;
            run.Add(price);
        }
        return run;
    }

    /// <summary>Fresh prices if they cover the current quarter hour, otherwise the last known ones (day-ahead prices do not change).</summary>
    public static (IReadOnlyList<TibberPriceForecastSlot> Prices, bool FromCache) ChoosePrices(
        IEnumerable<TibberPriceForecastSlot>? fresh, IEnumerable<TibberPriceForecastSlot>? cached,
        DateTimeOffset startUtc, DateTimeOffset horizonEndUtc)
    {
        var current = ContiguousFrom(fresh, startUtc, horizonEndUtc);
        if (current.Count > 0) return (current, false);
        return (ContiguousFrom(cached, startUtc, horizonEndUtc), true);
    }

    /// <summary>
    /// One PV value per price slot: fresh forecast, else the last successful forecast, else 0 kWh. Zero is the cautious
    /// choice: the plan then does not count on PV, and real surplus is still stored live by the execution.
    /// </summary>
    public static (IReadOnlyList<PvYieldForecastSlot> Pv, int FromCache, int Missing) CompletePv(
        IReadOnlyList<TibberPriceForecastSlot> prices, IEnumerable<PvYieldForecastSlot>? fresh, IEnumerable<PvYieldForecastSlot>? cached)
    {
        var freshBySlot = fresh?.Where(p => p.IsForecastAvailable).GroupBy(p => p.TimeSlot).ToDictionary(g => g.Key, g => g.First()) ?? [];
        var cachedBySlot = cached?.Where(p => p.IsForecastAvailable).GroupBy(p => p.TimeSlot).ToDictionary(g => g.Key, g => g.First()) ?? [];
        var fromCache = 0;
        var missing = 0;
        var result = prices.Select(price =>
        {
            if (freshBySlot.TryGetValue(price.TimeSlot, out var value)) return value;
            if (cachedBySlot.TryGetValue(price.TimeSlot, out value)) { fromCache++; return value; }
            missing++;
            return new PvYieldForecastSlot(price.TimeSlot, 0) { IsForecastAvailable = false };
        }).ToArray();
        return (result, fromCache, missing);
    }

    /// <summary>The quarter hour containing <paramref name="nowUtc"/>.</summary>
    public static DateTimeOffset QuarterStart(DateTimeOffset nowUtc) =>
        new(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute / 15 * 15, 0, TimeSpan.Zero);
}
