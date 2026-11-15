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
    /// Oldest stored forecast still accepted as a substitute. A day-old forecast for the same quarter hour is far
    /// closer to reality than "no PV at all"; anything older says little about the weather that is now expected.
    /// </summary>
    public static readonly TimeSpan MaximumPersistedPvAge = TimeSpan.FromHours(24);

    /// <summary>
    /// One PV value per price slot: fresh forecast, else the last successful forecast of this process, else 0 kWh.
    /// Kept for callers without stored forecasts; see the overload with <c>persisted</c> for the full chain.
    /// </summary>
    public static (IReadOnlyList<PvYieldForecastSlot> Pv, int FromCache, int Missing) CompletePv(
        IReadOnlyList<TibberPriceForecastSlot> prices, IEnumerable<PvYieldForecastSlot>? fresh, IEnumerable<PvYieldForecastSlot>? cached)
    {
        var (pv, fromCache, _, missing) = CompletePv(prices, fresh, cached, persisted: null);
        return (pv, fromCache, missing);
    }

    /// <summary>
    /// One PV value per price slot: fresh forecast, else the last successful forecast of this process, else the
    /// forecast stored in the database for that quarter hour (see <see cref="UsablePersistedPv"/>), else 0 kWh.
    /// Zero is cautious for discharging but not for buying: with "no PV tomorrow" the planner may charge from the grid
    /// for a day the PV would cover. After a restart the process-local forecast is gone, so the stored forecast is
    /// what keeps a failing provider from producing such a plan. Zero remains the last resort and is reported as
    /// missing; real surplus is still stored live by the execution.
    /// </summary>
    public static (IReadOnlyList<PvYieldForecastSlot> Pv, int FromCache, int FromPersisted, int Missing) CompletePv(
        IReadOnlyList<TibberPriceForecastSlot> prices, IEnumerable<PvYieldForecastSlot>? fresh, IEnumerable<PvYieldForecastSlot>? cached,
        IEnumerable<PvYieldForecastSlot>? persisted)
    {
        static Dictionary<ForecastTimeSlot, PvYieldForecastSlot> BySlot(IEnumerable<PvYieldForecastSlot>? slots) =>
            slots?.Where(p => p.IsForecastAvailable).GroupBy(p => p.TimeSlot).ToDictionary(g => g.Key, g => g.First()) ?? [];
        var freshBySlot = BySlot(fresh);
        var cachedBySlot = BySlot(cached);
        var persistedBySlot = BySlot(persisted);
        var fromCache = 0;
        var fromPersisted = 0;
        var missing = 0;
        var result = prices.Select(price =>
        {
            if (freshBySlot.TryGetValue(price.TimeSlot, out var value)) return value;
            if (cachedBySlot.TryGetValue(price.TimeSlot, out value)) { fromCache++; return value; }
            if (persistedBySlot.TryGetValue(price.TimeSlot, out value)) { fromPersisted++; return value; }
            missing++;
            return new PvYieldForecastSlot(price.TimeSlot, 0) { IsForecastAvailable = false };
        }).ToArray();
        return (result, fromCache, fromPersisted, missing);
    }

    /// <summary>
    /// Stored quarter-hour forecasts that may replace a missing forecast: captured at most
    /// <see cref="MaximumPersistedPvAge"/> ago, not in the future (clock change) and with a plausible value.
    /// Snapshots without a capture time are never used, because their age cannot be bounded.
    /// </summary>
    public static IReadOnlyList<PvYieldForecastSlot> UsablePersistedPv(IEnumerable<PvForecastSnapshot>? snapshots, DateTimeOffset nowUtc) =>
        snapshots is null ? [] : snapshots
            .Where(s => s.CapturedAtUtc is { } captured && captured <= nowUtc.AddMinutes(1) && nowUtc - captured <= MaximumPersistedPvAge
                && s.ForecastKwh >= 0)
            .Select(s => new PvYieldForecastSlot(new ForecastTimeSlot(s.SlotStartUtc.ToUniversalTime(), s.SlotStartUtc.ToUniversalTime().AddMinutes(15)), s.ForecastKwh))
            .ToArray();

    /// <summary>The quarter hour containing <paramref name="nowUtc"/>.</summary>
    public static DateTimeOffset QuarterStart(DateTimeOffset nowUtc) =>
        new(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute / 15 * 15, 0, TimeSpan.Zero);
}
