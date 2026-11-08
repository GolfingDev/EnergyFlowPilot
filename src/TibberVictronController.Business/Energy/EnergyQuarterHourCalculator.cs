namespace TibberVictronController.Business.Energy;

/// <summary>
/// Integrates measured power points into quarter hours. A value holds until the next point, but at most for the
/// source's maximum gap: longer gaps stay gaps (no interpolation) and lower the coverage instead.
/// </summary>
public static class EnergyQuarterHourCalculator
{
    public static readonly TimeSpan LiveMaximumGap = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DecisionLogMaximumGap = TimeSpan.FromMinutes(2);
    private static readonly long SlotTicks = TimeSpan.FromMinutes(15).Ticks;
    private const double WattSecondsPerKwh = 3_600_000;

    public static DateTimeOffset SlotStart(DateTimeOffset utc) => new(utc.UtcTicks - utc.UtcTicks % SlotTicks, TimeSpan.Zero);

    public static IReadOnlyList<EnergyQuarterHour> Calculate(IReadOnlyList<EnergyPowerPoint> points, EnergySampleSource source,
        TimeSpan maximumGap, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset computedAtUtc)
    {
        var slots = new SortedDictionary<long, Accumulator>();
        var ordered = points.OrderBy(point => point.AtUtc).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var point = ordered[i];
            var start = point.AtUtc < fromUtc ? fromUtc : point.AtUtc;
            var holdUntil = point.AtUtc + maximumGap;
            var end = i + 1 < ordered.Length && ordered[i + 1].AtUtc < holdUntil ? ordered[i + 1].AtUtc : holdUntil;
            if (end > toUtc) end = toUtc;
            // Split at quarter-hour boundaries so each slot gets exactly its share.
            while (start < end)
            {
                var slotStart = SlotStart(start);
                var pieceEnd = slotStart.AddTicks(SlotTicks) < end ? slotStart.AddTicks(SlotTicks) : end;
                if (!slots.TryGetValue(slotStart.UtcTicks, out var accumulator))
                    slots[slotStart.UtcTicks] = accumulator = new Accumulator();
                accumulator.Add(point, (pieceEnd - start).TotalSeconds);
                start = pieceEnd;
            }
        }

        return slots.Select(entry => entry.Value.ToQuarterHour(new DateTimeOffset(entry.Key, TimeSpan.Zero), source, computedAtUtc)).ToArray();
    }

    /// <summary>
    /// One quarter hour from both sources: grid from the better covered one (live on a tie), balance and wallbox from
    /// the live samples, price from the decision log.
    /// </summary>
    public static EnergyQuarterHour Combine(EnergyQuarterHour? live, EnergyQuarterHour? log)
    {
        if (live is null) return log!;
        if (log is null) return live;
        var grid = log.GridCoveredSeconds > live.GridCoveredSeconds ? log : live;
        return live with
        {
            GridSource = grid.GridSource,
            GridCoveredSeconds = grid.GridCoveredSeconds,
            GridImportKwh = grid.GridImportKwh,
            GridExportKwh = grid.GridExportKwh,
            PriceCoveredSeconds = log.PriceCoveredSeconds,
            PricePerKwh = log.PricePerKwh
        };
    }

    /// <summary>
    /// Keeps per group the more complete value, so a recalculation from partly deleted raw data never replaces better
    /// stored values; on equal coverage the new value wins, which makes repeated calculations idempotent.
    /// </summary>
    public static EnergyQuarterHour Merge(EnergyQuarterHour? existing, EnergyQuarterHour candidate)
    {
        if (existing is null) return candidate;
        var result = candidate;
        if (existing.GridCoveredSeconds > candidate.GridCoveredSeconds)
            result = result with
            {
                GridSource = existing.GridSource,
                GridCoveredSeconds = existing.GridCoveredSeconds,
                GridImportKwh = existing.GridImportKwh,
                GridExportKwh = existing.GridExportKwh
            };
        if (existing.BalanceCoveredSeconds > candidate.BalanceCoveredSeconds)
            result = result with
            {
                BalanceCoveredSeconds = existing.BalanceCoveredSeconds,
                HouseConsumptionKwh = existing.HouseConsumptionKwh,
                PvProductionKwh = existing.PvProductionKwh,
                PrimaryBatteryChargeKwh = existing.PrimaryBatteryChargeKwh,
                PrimaryBatteryDischargeKwh = existing.PrimaryBatteryDischargeKwh,
                SupplementaryBatteryChargeKwh = existing.SupplementaryBatteryChargeKwh,
                SupplementaryBatteryDischargeKwh = existing.SupplementaryBatteryDischargeKwh
            };
        if (existing.EvCoveredSeconds > candidate.EvCoveredSeconds)
            result = result with { EvCoveredSeconds = existing.EvCoveredSeconds, EvChargingKwh = existing.EvChargingKwh };
        if (existing.PriceCoveredSeconds > candidate.PriceCoveredSeconds)
            result = result with { PriceCoveredSeconds = existing.PriceCoveredSeconds, PricePerKwh = existing.PricePerKwh };
        return result;
    }

    private sealed class Accumulator
    {
        private double gridSeconds, importWs, exportWs;
        private double balanceSeconds, houseWs, pvWs, primaryChargeWs, primaryDischargeWs, supplementaryChargeWs, supplementaryDischargeWs;
        private double evSeconds, evWs;
        private double priceSeconds;
        private decimal priceSum;

        public void Add(EnergyPowerPoint point, double seconds)
        {
            if (point.GridWatts is { } grid)
            {
                gridSeconds += seconds;
                if (grid > 0) importWs += grid * seconds;
                else exportWs -= grid * seconds;
            }

            // The house balance needs every flow at the same moment; otherwise energy would be missing or counted twice.
            if (point is { GridWatts: { } g, PrimaryBatteryWatts: { } primary, SupplementaryBatteryWatts: { } supplementary, SupplementaryPvWatts: { } pv })
            {
                balanceSeconds += seconds;
                houseWs += (g + pv - primary - supplementary) * seconds;
                pvWs += Math.Max(0, pv) * seconds;
                if (primary > 0) primaryChargeWs += primary * seconds;
                else primaryDischargeWs -= primary * seconds;
                if (supplementary > 0) supplementaryChargeWs += supplementary * seconds;
                else supplementaryDischargeWs -= supplementary * seconds;
            }

            if (point.EvWatts is { } ev)
            {
                evSeconds += seconds;
                evWs += Math.Max(0, ev) * seconds;
            }

            if (point.PricePerKwh is { } price)
            {
                priceSeconds += seconds;
                priceSum += price * (decimal)seconds;
            }
        }

        public EnergyQuarterHour ToQuarterHour(DateTimeOffset slotStartUtc, EnergySampleSource source, DateTimeOffset computedAtUtc)
        {
            static int Seconds(double value) => (int)Math.Min(EnergyQuarterHour.SlotSeconds, Math.Round(value));
            var hasBalance = balanceSeconds > 0;
            return new EnergyQuarterHour(slotStartUtc)
            {
                GridSource = source,
                GridCoveredSeconds = Seconds(gridSeconds),
                GridImportKwh = importWs / WattSecondsPerKwh,
                GridExportKwh = exportWs / WattSecondsPerKwh,
                BalanceCoveredSeconds = Seconds(balanceSeconds),
                HouseConsumptionKwh = hasBalance ? houseWs / WattSecondsPerKwh : null,
                PvProductionKwh = hasBalance ? pvWs / WattSecondsPerKwh : null,
                PrimaryBatteryChargeKwh = hasBalance ? primaryChargeWs / WattSecondsPerKwh : null,
                PrimaryBatteryDischargeKwh = hasBalance ? primaryDischargeWs / WattSecondsPerKwh : null,
                SupplementaryBatteryChargeKwh = hasBalance ? supplementaryChargeWs / WattSecondsPerKwh : null,
                SupplementaryBatteryDischargeKwh = hasBalance ? supplementaryDischargeWs / WattSecondsPerKwh : null,
                EvCoveredSeconds = Seconds(evSeconds),
                EvChargingKwh = evSeconds > 0 ? evWs / WattSecondsPerKwh : null,
                PriceCoveredSeconds = Seconds(priceSeconds),
                PricePerKwh = priceSeconds > 0 ? Math.Round(priceSum / (decimal)priceSeconds, 5) : null,
                ComputedAtUtc = computedAtUtc
            };
        }
    }
}
