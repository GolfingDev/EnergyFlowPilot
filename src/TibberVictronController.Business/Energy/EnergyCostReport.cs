using System.Globalization;

namespace TibberVictronController.Business.Energy;

public enum EnergyBucketSize
{
    QuarterHour,
    Hour,
    Day,
    Month,
    Quarter
}

/// <summary>
/// Sums of one evaluated window. Energy values are null where nothing was measured (never zero for "unknown");
/// coverages are shares of the evaluated time (0 to 1). Costs in EUR.
/// </summary>
public sealed record EnergyTotals
{
    public double EvaluatedHours { get; init; }
    public double GridCoverage { get; init; }
    public double BalanceCoverage { get; init; }
    public double? GridImportKwh { get; init; }
    public double? GridExportKwh { get; init; }
    public double? HouseConsumptionKwh { get; init; }
    public double? PvProductionKwh { get; init; }
    public double? PrimaryBatteryChargeKwh { get; init; }
    public double? PrimaryBatteryDischargeKwh { get; init; }
    public double? SupplementaryBatteryChargeKwh { get; init; }
    public double? SupplementaryBatteryDischargeKwh { get; init; }
    public double? EvChargingKwh { get; init; }

    /// <summary>Cost of the grid import that has a valid price; see <see cref="UnpricedImportKwh"/> for the rest.</summary>
    public decimal? ImportCost { get; init; }
    public double UnpricedImportKwh { get; init; }
    /// <summary>Import cost divided by the priced import energy.</summary>
    public decimal? WeightedPricePerKwh { get; init; }
    public decimal? BaseFee { get; init; }
    public bool BaseFeeComplete { get; init; }
    public decimal? FeedInCompensation { get; init; }
    public bool FeedInComplete { get; init; }
    /// <summary>Import cost + base fee - feed-in compensation; null when one of them is entirely unknown.</summary>
    public decimal? TotalAfterCompensation { get; init; }

    /// <summary>Import cost covers the whole window: grid measured sufficiently and every imported kWh priced.</summary>
    public bool ImportCostComplete { get; init; }
    public bool TotalComplete { get; init; }
}

public sealed record EnergyBucket(DateTimeOffset StartUtc, DateTimeOffset EndUtc, EnergyTotals Totals);

/// <summary>Daily averages over the whole days with sufficient coverage only; the other days are listed with the reason.</summary>
public sealed record EnergyDailyAverages(
    int CompleteDays,
    int GridDays,
    int BalanceDays,
    double? HouseConsumptionKwhPerDay,
    double? GridImportKwhPerDay,
    decimal? ImportCostPerDay,
    IReadOnlyList<EnergyExcludedDay> ExcludedDays);

public sealed record EnergyExcludedDay(DateOnly Day, string Reason);

/// <summary>Change against the previous period; null where either side is not sufficiently measured.</summary>
public sealed record EnergyChange(double? Absolute, double? Percent);

public sealed record EnergyComparison(
    EnergyChange HouseConsumptionKwh,
    EnergyChange GridImportKwh,
    EnergyChange GridExportKwh,
    EnergyChange ImportCost,
    EnergyChange TotalAfterCompensation,
    string? Problem);

public sealed record EnergyCostReport(
    EnergyPeriod Period,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset EvaluatedUntilUtc,
    EnergyTotals Totals,
    EnergyPeriod PreviousPeriod,
    DateTimeOffset PreviousFromUtc,
    DateTimeOffset PreviousToUtc,
    EnergyTotals Previous,
    EnergyComparison Comparison,
    EnergyDailyAverages Averages,
    EnergyBucketSize SeriesSize,
    IReadOnlyList<EnergyBucket> Series,
    EnergyBucketSize CostSize,
    IReadOnlyList<EnergyBucket> CostBuckets,
    IReadOnlyList<string> Notes);

public sealed record EnergyCostReportInput(
    EnergyPeriod Period,
    DateTimeOffset NowUtc,
    TimeZoneInfo Zone,
    IReadOnlyList<EnergyQuarterHour> QuarterHours,
    IReadOnlyList<TariffPeriod> Tariffs)
{
    /// <summary>First measured house balance of the whole history, for the note on older periods.</summary>
    public DateTimeOffset? FirstBalanceMeasurementUtc { get; init; }
}

/// <summary>
/// Builds the electricity cost and consumption report of a period from the stored, measured quarter hours. Costs are
/// measured import x valid price; grid charging of the batteries is paid at import time and the battery savings are
/// not subtracted again. Base fees are spread over the evaluated time, feed-in is paid per exported kWh.
/// </summary>
public static class EnergyCostReportBuilder
{
    /// <summary>A day or window counts as measured when at least this share of its time is covered.</summary>
    public const double SufficientCoverage = 0.95;
    private const double UnpricedToleranceKwh = 0.005;
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static EnergyCostReport Build(EnergyCostReportInput input)
    {
        var zone = input.Zone;
        var quarters = input.QuarterHours.OrderBy(slot => slot.SlotStartUtc).ToArray();
        var (fromUtc, toUtc) = EnergyPeriods.ToUtc(input.Period, zone);
        var until = input.NowUtc < toUtc ? (input.NowUtc > fromUtc ? input.NowUtc : fromUtc) : toUtc;
        var totals = Totals(quarters, fromUtc, until, input.Tariffs, zone);

        var previousPeriod = EnergyPeriods.Shift(input.Period, -1);
        var (previousFrom, _) = EnergyPeriods.ToUtc(previousPeriod, zone);
        var previousTo = EnergyPeriods.SameElapsedEnd(input.Period, previousPeriod, until, zone);
        var previous = Totals(quarters, previousFrom, previousTo, input.Tariffs, zone);

        var seriesSize = input.Period.Days <= 1 ? EnergyBucketSize.QuarterHour : input.Period.Days <= 7 ? EnergyBucketSize.Hour
            : input.Period.Days <= 400 ? EnergyBucketSize.Day : EnergyBucketSize.Month;
        var costSize = input.Period.Days <= 1 ? EnergyBucketSize.Hour : input.Period.Days <= 62 ? EnergyBucketSize.Day
            : input.Period.Days <= 731 ? EnergyBucketSize.Month : EnergyBucketSize.Quarter;
        EnergyBucket[] BucketsOf(EnergyBucketSize size) => Buckets(input.Period, size, zone)
            .Where(bucket => bucket.Start < until)
            .Select(bucket => new EnergyBucket(bucket.Start, bucket.End,
                Totals(quarters, bucket.Start, bucket.End < until ? bucket.End : until, input.Tariffs, zone)))
            .ToArray();

        var averages = Averages(quarters, input.Period, until, input.Tariffs, zone);
        return new EnergyCostReport(input.Period, fromUtc, toUtc, until, totals, previousPeriod, previousFrom, previousTo, previous,
            Compare(totals, previous), averages, seriesSize, BucketsOf(seriesSize), costSize, BucketsOf(costSize),
            Notes(totals, averages, input.Tariffs, input.Period,
                input.FirstBalanceMeasurementUtc is { } first ? EnergyPeriods.LocalDay(first, zone) : null));
    }

    public static EnergyTotals Totals(IReadOnlyList<EnergyQuarterHour> sortedQuarters, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        IReadOnlyList<TariffPeriod> tariffs, TimeZoneInfo zone)
    {
        var seconds = Math.Max(0, (toUtc - fromUtc).TotalSeconds);
        if (seconds <= 0) return new EnergyTotals();
        var slots = Slice(sortedQuarters, fromUtc, toUtc);
        double gridSeconds = 0, balanceSeconds = 0, import = 0, export = 0, pricedImport = 0, unpriced = 0;
        double house = 0, pv = 0, primaryCharge = 0, primaryDischarge = 0, supplementaryCharge = 0, supplementaryDischarge = 0, ev = 0;
        var anyEv = false;
        decimal cost = 0, feedIn = 0;
        bool anyPrice = false, feedInComplete = true, anyFeedIn = false;
        foreach (var slot in slots)
        {
            if (slot.GridCoveredSeconds > 0)
            {
                gridSeconds += slot.GridCoveredSeconds;
                import += slot.GridImportKwh;
                export += slot.GridExportKwh;
                if (slot.PricePerKwh is { } price)
                {
                    anyPrice = true;
                    cost += price * (decimal)slot.GridImportKwh;
                    pricedImport += slot.GridImportKwh;
                }
                else
                    unpriced += slot.GridImportKwh;

                if (slot.GridExportKwh > 0)
                {
                    var rate = TariffPeriods.ValidOn(tariffs, EnergyPeriods.LocalDay(slot.SlotStartUtc, zone))?.FeedInPricePerKwh;
                    if (rate is { } feedInRate)
                    {
                        feedIn += feedInRate * (decimal)slot.GridExportKwh;
                        anyFeedIn = true;
                    }
                    else
                        feedInComplete = false;
                }
            }

            if (slot.BalanceCoveredSeconds > 0)
            {
                balanceSeconds += slot.BalanceCoveredSeconds;
                house += slot.HouseConsumptionKwh ?? 0;
                pv += slot.PvProductionKwh ?? 0;
                primaryCharge += slot.PrimaryBatteryChargeKwh ?? 0;
                primaryDischarge += slot.PrimaryBatteryDischargeKwh ?? 0;
                supplementaryCharge += slot.SupplementaryBatteryChargeKwh ?? 0;
                supplementaryDischarge += slot.SupplementaryBatteryDischargeKwh ?? 0;
            }

            if (slot.EvChargingKwh is { } evKwh)
            {
                anyEv = true;
                ev += evKwh;
            }
        }

        var (baseFee, baseFeeComplete) = BaseFee(fromUtc, toUtc, tariffs, zone);
        var gridCoverage = Math.Min(1, gridSeconds / seconds);
        var hasGrid = gridSeconds > 0;
        var hasBalance = balanceSeconds > 0;
        decimal? importCost = anyPrice ? cost : hasGrid && import < UnpricedToleranceKwh ? 0 : null;
        decimal? feedInCompensation = !hasGrid ? null : export < UnpricedToleranceKwh ? 0 : anyFeedIn ? feedIn : null;
        var importComplete = hasGrid && gridCoverage >= SufficientCoverage && unpriced < UnpricedToleranceKwh;
        var exportComplete = feedInComplete || export < UnpricedToleranceKwh;
        return new EnergyTotals
        {
            EvaluatedHours = seconds / 3600,
            GridCoverage = gridCoverage,
            BalanceCoverage = Math.Min(1, balanceSeconds / seconds),
            GridImportKwh = hasGrid ? import : null,
            GridExportKwh = hasGrid ? export : null,
            HouseConsumptionKwh = hasBalance ? house : null,
            PvProductionKwh = hasBalance ? pv : null,
            PrimaryBatteryChargeKwh = hasBalance ? primaryCharge : null,
            PrimaryBatteryDischargeKwh = hasBalance ? primaryDischarge : null,
            SupplementaryBatteryChargeKwh = hasBalance ? supplementaryCharge : null,
            SupplementaryBatteryDischargeKwh = hasBalance ? supplementaryDischarge : null,
            EvChargingKwh = anyEv ? ev : null,
            ImportCost = importCost,
            UnpricedImportKwh = unpriced,
            WeightedPricePerKwh = pricedImport > UnpricedToleranceKwh ? cost / (decimal)pricedImport : null,
            BaseFee = baseFee,
            BaseFeeComplete = baseFeeComplete,
            FeedInCompensation = feedInCompensation,
            FeedInComplete = exportComplete,
            TotalAfterCompensation = importCost is { } c && baseFee is { } b && feedInCompensation is { } f ? c + b - f : null,
            ImportCostComplete = importComplete,
            TotalComplete = importComplete && baseFeeComplete && exportComplete
        };
    }

    // The base fee of each local day, for the part of the day inside the window (DST days have 23 or 25 hours).
    private static (decimal? Fee, bool Complete) BaseFee(DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<TariffPeriod> tariffs, TimeZoneInfo zone)
    {
        decimal sum = 0;
        bool any = false, complete = true;
        for (var day = EnergyPeriods.LocalDay(fromUtc, zone); EnergyPeriods.StartOfDayUtc(day, zone) < toUtc; day = day.AddDays(1))
        {
            var dayStart = EnergyPeriods.StartOfDayUtc(day, zone);
            var dayEnd = EnergyPeriods.StartOfDayUtc(day.AddDays(1), zone);
            var overlap = (Min(dayEnd, toUtc) - Max(dayStart, fromUtc)).TotalSeconds;
            if (overlap <= 0) continue;
            if (TariffPeriods.DailyBaseFee(TariffPeriods.ValidOn(tariffs, day), day) is { } daily)
            {
                sum += daily * (decimal)(overlap / (dayEnd - dayStart).TotalSeconds);
                any = true;
            }
            else
                complete = false;
        }

        return (any ? sum : null, complete && any);
    }

    private static EnergyDailyAverages Averages(IReadOnlyList<EnergyQuarterHour> quarters, EnergyPeriod period, DateTimeOffset untilUtc,
        IReadOnlyList<TariffPeriod> tariffs, TimeZoneInfo zone)
    {
        var excluded = new List<EnergyExcludedDay>();
        var gridDays = new List<EnergyTotals>();
        var balanceDays = new List<EnergyTotals>();
        var costDays = new List<EnergyTotals>();
        for (var day = period.Start; day < period.EndExclusive; day = day.AddDays(1))
        {
            var dayStart = EnergyPeriods.StartOfDayUtc(day, zone);
            var dayEnd = EnergyPeriods.StartOfDayUtc(day.AddDays(1), zone);
            // Only whole days: today is still running.
            if (dayEnd > untilUtc) break;
            var totals = Totals(quarters, dayStart, dayEnd, tariffs, zone);
            if (totals.GridCoverage >= SufficientCoverage) gridDays.Add(totals);
            if (totals.BalanceCoverage >= SufficientCoverage) balanceDays.Add(totals);
            if (totals.ImportCostComplete) costDays.Add(totals);
            if (totals.GridCoverage < SufficientCoverage)
                excluded.Add(new EnergyExcludedDay(day, totals.GridCoverage == 0 ? "keine Messung"
                    : string.Format(German, "Netzmessung nur zu {0:0} %", totals.GridCoverage * 100)));
            else if (!totals.ImportCostComplete)
                excluded.Add(new EnergyExcludedDay(day, string.Format(German, "für {0:0.0} kWh fehlt der Preis", totals.UnpricedImportKwh)));
        }

        return new EnergyDailyAverages(costDays.Count, gridDays.Count, balanceDays.Count,
            balanceDays.Count == 0 ? null : balanceDays.Average(day => day.HouseConsumptionKwh ?? 0),
            gridDays.Count == 0 ? null : gridDays.Average(day => day.GridImportKwh ?? 0),
            costDays.Count == 0 ? null : costDays.Average(day => day.ImportCost ?? 0),
            excluded);
    }

    private static EnergyComparison Compare(EnergyTotals current, EnergyTotals previous)
    {
        static EnergyChange Change(double? now, double? before, bool comparable)
        {
            if (!comparable || now is not { } a || before is not { } b) return new EnergyChange(null, null);
            return new EnergyChange(a - b, Math.Abs(b) < 1e-9 ? null : (a - b) / Math.Abs(b) * 100);
        }

        var grid = current.GridCoverage >= SufficientCoverage && previous.GridCoverage >= SufficientCoverage;
        var balance = current.BalanceCoverage >= SufficientCoverage && previous.BalanceCoverage >= SufficientCoverage;
        var cost = current.ImportCostComplete && previous.ImportCostComplete;
        var total = current.TotalComplete && previous.TotalComplete;
        var problem = previous.GridCoverage == 0 ? "Für den Vergleichszeitraum gibt es keine Messwerte."
            : !grid ? "Der Vergleichszeitraum ist nicht ausreichend gemessen."
            : !balance ? "Hausverbrauch im Vergleichszeitraum nicht ausreichend gemessen." : null;
        return new EnergyComparison(
            Change(current.HouseConsumptionKwh, previous.HouseConsumptionKwh, balance),
            Change(current.GridImportKwh, previous.GridImportKwh, grid),
            Change(current.GridExportKwh, previous.GridExportKwh, grid),
            Change((double?)current.ImportCost, (double?)previous.ImportCost, cost),
            Change((double?)current.TotalAfterCompensation, (double?)previous.TotalAfterCompensation, total),
            problem);
    }

    private static IReadOnlyList<string> Notes(EnergyTotals totals, EnergyDailyAverages averages, IReadOnlyList<TariffPeriod> tariffs, EnergyPeriod period,
        DateOnly? firstBalanceDay)
    {
        var notes = new List<string>();
        if (totals.GridCoverage == 0) notes.Add("Für diesen Zeitraum gibt es keine Messwerte.");
        else if (totals.BalanceCoverage < totals.GridCoverage - 0.05)
            notes.Add(firstBalanceDay is { } since && since > period.Start
                ? string.Format(German, "Hausverbrauch, PV und Akkus werden erst seit {0:dd.MM.yyyy} gemessen; davor sind nur Netzbezug, Einspeisung und Kosten bekannt.", since)
                : "Hausverbrauch, PV und Akkus sind in diesem Zeitraum nur teilweise gemessen.");
        if (totals.UnpricedImportKwh >= UnpricedToleranceKwh)
            notes.Add(string.Format(German, "Für {0:0.0} kWh Netzbezug fehlt der Preis; die Bezugskosten sind Teilkosten.", totals.UnpricedImportKwh));
        if (tariffs.Count == 0) notes.Add("Grundgebühr und Einspeisevergütung sind nicht hinterlegt (Tarifzeiträume).");
        else if (!totals.BaseFeeComplete || !totals.FeedInComplete)
            notes.Add("Für einen Teil des Zeitraums fehlen Grundgebühr oder Einspeisevergütung in den Tarifzeiträumen.");
        if (averages.ExcludedDays.Count > 0 && period.Days > 1)
            notes.Add(averages.ExcludedDays.Count == 1 ? "Ein Tag ohne ausreichende Messung zählt nicht zu den Tagesdurchschnitten."
                : $"{averages.ExcludedDays.Count} Tage ohne ausreichende Messung zählen nicht zu den Tagesdurchschnitten.");
        return notes;
    }

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Buckets(EnergyPeriod period, EnergyBucketSize size, TimeZoneInfo zone)
    {
        var (fromUtc, toUtc) = EnergyPeriods.ToUtc(period, zone);
        switch (size)
        {
            case EnergyBucketSize.QuarterHour:
            case EnergyBucketSize.Hour:
                var step = size == EnergyBucketSize.QuarterHour ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(1);
                for (var at = fromUtc; at < toUtc; at += step) yield return (at, at + step < toUtc ? at + step : toUtc);
                yield break;
            case EnergyBucketSize.Day:
                for (var day = period.Start; day < period.EndExclusive; day = day.AddDays(1))
                    yield return (EnergyPeriods.StartOfDayUtc(day, zone), EnergyPeriods.StartOfDayUtc(day.AddDays(1), zone));
                yield break;
            default:
                var months = size == EnergyBucketSize.Month ? 1 : 3;
                var first = new DateOnly(period.Start.Year, size == EnergyBucketSize.Month ? period.Start.Month : (period.Start.Month - 1) / 3 * 3 + 1, 1);
                for (var start = first; start < period.EndExclusive; start = start.AddMonths(months))
                {
                    var bucketStart = start < period.Start ? period.Start : start;
                    var end = start.AddMonths(months) > period.EndExclusive ? period.EndExclusive : start.AddMonths(months);
                    yield return (EnergyPeriods.StartOfDayUtc(bucketStart, zone), EnergyPeriods.StartOfDayUtc(end, zone));
                }

                yield break;
        }
    }

    private static IEnumerable<EnergyQuarterHour> Slice(IReadOnlyList<EnergyQuarterHour> sorted, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        int low = 0, high = sorted.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle].SlotStartUtc < fromUtc) low = middle + 1;
            else high = middle;
        }

        for (var i = low; i < sorted.Count && sorted[i].SlotStartUtc < toUtc; i++) yield return sorted[i];
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
