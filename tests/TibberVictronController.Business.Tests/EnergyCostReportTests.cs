using TibberVictronController.Business.Energy;

namespace TibberVictronController.Business.Tests;

public sealed class EnergyCostReportTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    [Fact]
    public void PeriodsFollowTheLocalCalendarIncludingDaylightSavingDays()
    {
        Assert.Equal(new DateOnly(2026, 11, 2), EnergyPeriods.Containing(EnergyPeriodKind.Week, new DateOnly(2026, 11, 8)).Start); // Sunday -> Monday
        Assert.Equal(new DateOnly(2026, 10, 1), EnergyPeriods.Containing(EnergyPeriodKind.Quarter, new DateOnly(2026, 11, 8)).Start);
        Assert.Equal(new DateOnly(2026, 7, 1), EnergyPeriods.Shift(EnergyPeriods.Containing(EnergyPeriodKind.Quarter, new DateOnly(2026, 11, 8)), -1).Start);
        Assert.Equal(new DateOnly(2026, 2, 1), EnergyPeriods.Shift(EnergyPeriods.Containing(EnergyPeriodKind.Month, new DateOnly(2026, 3, 31)), -1).Start);

        // 25 October 2026: clocks go back, the day has 25 hours; 29 March 2026 has 23.
        var (autumnFrom, autumnTo) = EnergyPeriods.ToUtc(EnergyPeriods.Containing(EnergyPeriodKind.Day, new DateOnly(2026, 10, 25)), Berlin);
        var (springFrom, springTo) = EnergyPeriods.ToUtc(EnergyPeriods.Containing(EnergyPeriodKind.Day, new DateOnly(2026, 3, 29)), Berlin);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero), autumnFrom);
        Assert.Equal(25, (autumnTo - autumnFrom).TotalHours);
        Assert.Equal(23, (springTo - springFrom).TotalHours);
    }

    [Fact]
    public void RunningPeriodIsComparedWithTheSameElapsedPartOfThePreviousOne()
    {
        var november = EnergyPeriods.Containing(EnergyPeriodKind.Month, new DateOnly(2026, 11, 8));
        var october = EnergyPeriods.Shift(november, -1);
        // 8 November 16:00 local (UTC+1) -> 8 October 16:00 local (UTC+2, summer time).
        var end = EnergyPeriods.SameElapsedEnd(november, october, new DateTimeOffset(2026, 11, 8, 15, 0, 0, TimeSpan.Zero), Berlin);

        Assert.Equal(new DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void CostsUseTheValidPriceIncludingNegativeOnesAndTheTariffOfEachDay()
    {
        var day = new DateOnly(2026, 6, 30);
        var (from, to) = EnergyPeriods.ToUtc(EnergyPeriods.Custom(day, day.AddDays(1)), Berlin);
        // Two full days of 1 kWh import and 0.5 kWh export per quarter hour; price 0.30, at night -0.10.
        var quarters = Quarters(from, to, slot => slot.Hour < 2 ? -0.10m : 0.30m, import: 1, export: 0.5);
        var tariffs = new[]
        {
            new TariffPeriod(new DateOnly(2026, 5, 1), "Alt", 30m, 0.08m),
            new TariffPeriod(new DateOnly(2026, 7, 1), "Neu", 31m, 0.10m)
        };

        var report = EnergyCostReportBuilder.Build(new EnergyCostReportInput(EnergyPeriods.Custom(day, day.AddDays(1)), to.AddDays(1), Berlin, quarters, tariffs));

        // Per day 96 kWh import; 0:00-2:00 local equals 22:00-24:00 UTC the day before; count it per quarter hour instead.
        var expectedCost = quarters.Sum(slot => slot.PricePerKwh!.Value * 1m);
        Assert.Equal(expectedCost, report.Totals.ImportCost);
        Assert.Equal(192, report.Totals.GridImportKwh!.Value, 6);
        // Base fee: 30 EUR / 30 days of June + 31 EUR / 31 days of July.
        Assert.Equal(2m, Math.Round(report.Totals.BaseFee!.Value, 6));
        // Feed-in: 48 kWh per day at 0.08 and 0.10.
        Assert.Equal(48 * 0.08m + 48 * 0.10m, Math.Round(report.Totals.FeedInCompensation!.Value, 6));
        Assert.Equal(report.Totals.ImportCost + 2m - report.Totals.FeedInCompensation, Math.Round(report.Totals.TotalAfterCompensation!.Value, 6));
        Assert.True(report.Totals.TotalComplete);
        Assert.Equal(EnergyBucketSize.Day, report.CostSize);
        Assert.Equal(2, report.CostBuckets.Count);
    }

    [Fact]
    public void BaseFeeOfADaylightSavingDayIsSpreadOverItsTwentyFiveHours()
    {
        var day = new DateOnly(2026, 10, 25);
        var (from, _) = EnergyPeriods.ToUtc(EnergyPeriods.Containing(EnergyPeriodKind.Day, day), Berlin);
        var tariffs = new[] { new TariffPeriod(new DateOnly(2026, 1, 1), "Anbieter", 31m, null) };

        // Five of the 25 hours evaluated: 1/5 of the daily fee of 1 EUR.
        var totals = EnergyCostReportBuilder.Totals([], from, from.AddHours(5), tariffs, Berlin);

        Assert.Equal(0.2m, Math.Round(totals.BaseFee!.Value, 6));
        Assert.True(totals.BaseFeeComplete);
        Assert.Null(totals.FeedInCompensation); // nothing measured: unknown, not zero
        Assert.Null(totals.GridImportKwh);
    }

    [Fact]
    public void MissingPricesAndGapsMakePartialCostsAndExcludeDaysFromAverages()
    {
        var start = new DateOnly(2026, 9, 1);
        var period = EnergyPeriods.Custom(start, start.AddDays(2));
        var (from, to) = EnergyPeriods.ToUtc(period, Berlin);
        var quarters = Quarters(from, to, _ => 0.30m, import: 0.5, export: 0)
            // Day 2: no price in the morning; day 3: measured only half of the day.
            .Select(slot => EnergyPeriods.LocalDay(slot.SlotStartUtc, Berlin) == start.AddDays(1) && TimeZoneInfo.ConvertTime(slot.SlotStartUtc, Berlin).Hour < 6
                ? slot with { PricePerKwh = null, PriceCoveredSeconds = 0 } : slot)
            .Where(slot => EnergyPeriods.LocalDay(slot.SlotStartUtc, Berlin) != start.AddDays(2) || TimeZoneInfo.ConvertTime(slot.SlotStartUtc, Berlin).Hour < 12)
            .ToArray();

        var report = EnergyCostReportBuilder.Build(new EnergyCostReportInput(period, to.AddDays(1), Berlin, quarters, []));

        Assert.False(report.Totals.ImportCostComplete);
        Assert.Equal(12, report.Totals.UnpricedImportKwh, 6);
        Assert.Equal(1, report.Averages.CompleteDays);
        Assert.Equal(48 * 0.30m, report.Averages.ImportCostPerDay);
        Assert.Equal(2, report.Averages.ExcludedDays.Count);
        Assert.Contains(report.Notes, note => note.Contains("Teilkosten"));
        Assert.Contains(report.Notes, note => note.Contains("nicht hinterlegt"));
        Assert.Null(report.Totals.BaseFee);
        Assert.Null(report.Totals.TotalAfterCompensation);
    }

    [Fact]
    public void ComparisonNeedsSufficientMeasurementOnBothSides()
    {
        // Two weeks without a daylight saving change, so both have the same number of quarter hours.
        var november = EnergyPeriods.Containing(EnergyPeriodKind.Week, new DateOnly(2026, 11, 4));
        var (from, to) = EnergyPeriods.ToUtc(november, Berlin);
        var (previousFrom, previousTo) = EnergyPeriods.ToUtc(EnergyPeriods.Shift(november, -1), Berlin);
        var quarters = Quarters(previousFrom, previousTo, _ => 0.25m, import: 0.25, export: 0)
            .Concat(Quarters(from, to, _ => 0.30m, import: 0.5, export: 0)).ToArray();

        var report = EnergyCostReportBuilder.Build(new EnergyCostReportInput(november, to.AddDays(1), Berlin, quarters, []));
        var withoutPrevious = EnergyCostReportBuilder.Build(new EnergyCostReportInput(november, to.AddDays(1), Berlin,
            quarters.Where(slot => slot.SlotStartUtc >= from).ToArray(), []));

        Assert.Equal(100, report.Comparison.GridImportKwh.Percent!.Value, 1); // 0.5 against 0.25 kWh per quarter hour
        Assert.Null(withoutPrevious.Comparison.GridImportKwh.Absolute);
        Assert.NotNull(withoutPrevious.Comparison.Problem);
    }

    [Fact]
    public void CsvHasUnitsGermanNumbersAndEmptyCellsForUnknownValues()
    {
        var day = new DateOnly(2026, 9, 1);
        var (from, to) = EnergyPeriods.ToUtc(EnergyPeriods.Containing(EnergyPeriodKind.Day, day), Berlin);
        var report = EnergyCostReportBuilder.Build(new EnergyCostReportInput(EnergyPeriods.Containing(EnergyPeriodKind.Day, day), to, Berlin,
            Quarters(from, to, _ => 0.3m, import: 0.25, export: 0), []));

        var csv = EnergyCostCsv.Write(report, Berlin);
        var lines = csv.TrimEnd().Split(Environment.NewLine);

        Assert.Contains("Bezugskosten (EUR)", lines[0]);
        Assert.Contains("Abdeckung Netzmessung (%)", lines[0]);
        Assert.Equal(26, lines.Length); // header, 24 hours, sum
        Assert.Contains(";7,20;", lines[^1]); // 24 kWh x 0.30 EUR
        Assert.Contains(";;", lines[^1]); // no house balance, no base fee
    }

    [Fact]
    public void TariffPeriodsAreValidated()
    {
        Assert.Throws<ArgumentException>(() => TariffPeriods.Validate([new TariffPeriod(new DateOnly(2026, 1, 1), "", 10m, null)]));
        Assert.Throws<ArgumentException>(() => TariffPeriods.Validate([new TariffPeriod(new DateOnly(2026, 1, 1), "A", -1m, null)]));
        Assert.Throws<ArgumentException>(() => TariffPeriods.Validate([
            new TariffPeriod(new DateOnly(2026, 1, 1), "A", 10m, null), new TariffPeriod(new DateOnly(2026, 1, 1), "B", 10m, null)]));
        var periods = TariffPeriods.Validate([new TariffPeriod(new DateOnly(2026, 11, 12), "Octopus", 13.44m, null), new TariffPeriod(new DateOnly(2026, 5, 1), " Tibber ", null, 0.08m)]);
        Assert.Equal("Tibber", periods[0].Supplier);
        Assert.Equal(periods, TariffPeriods.Parse(TariffPeriods.Serialize(periods)));
        Assert.Empty(TariffPeriods.Parse("kein json"));
    }

    private static IReadOnlyList<EnergyQuarterHour> Quarters(DateTimeOffset from, DateTimeOffset to, Func<DateTimeOffset, decimal> price, double import, double export)
    {
        var result = new List<EnergyQuarterHour>();
        for (var at = from; at < to; at = at.AddMinutes(15))
            result.Add(new EnergyQuarterHour(at)
            {
                GridCoveredSeconds = 900, GridImportKwh = import, GridExportKwh = export, PriceCoveredSeconds = 900,
                PricePerKwh = price(TimeZoneInfo.ConvertTime(at, Berlin))
            });
        return result;
    }
}
