using System.Globalization;
using TibberVictronController.Business.Energy;

namespace TibberVictronController.Api.Energy;

public sealed record TariffPeriodDto(DateOnly ValidFrom, string? Supplier, decimal? MonthlyBaseFee, decimal? FeedInPricePerKwh);

public sealed record EnergyTotalsDto(
    double EvaluatedHours,
    double GridCoveragePercent,
    double BalanceCoveragePercent,
    double? HouseConsumptionKwh,
    double? GridImportKwh,
    double? GridExportKwh,
    double? PvProductionKwh,
    double? PrimaryBatteryChargeKwh,
    double? PrimaryBatteryDischargeKwh,
    double? SupplementaryBatteryChargeKwh,
    double? SupplementaryBatteryDischargeKwh,
    double? EvChargingKwh,
    decimal? ImportCost,
    double UnpricedImportKwh,
    decimal? WeightedPricePerKwh,
    decimal? BaseFee,
    bool BaseFeeComplete,
    decimal? FeedInCompensation,
    bool FeedInComplete,
    decimal? TotalAfterCompensation,
    bool ImportCostComplete,
    bool TotalComplete);

public sealed record EnergyBucketDto(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Label, EnergyTotalsDto Totals);

public sealed record EnergyChangeDto(double? Absolute, double? Percent);

public sealed record EnergyComparisonDto(
    EnergyChangeDto HouseConsumptionKwh,
    EnergyChangeDto GridImportKwh,
    EnergyChangeDto GridExportKwh,
    EnergyChangeDto ImportCost,
    EnergyChangeDto TotalAfterCompensation,
    string? Problem);

public sealed record EnergyExcludedDayDto(DateOnly Day, string Reason);

public sealed record EnergyAveragesDto(
    int CompleteDays,
    int GridDays,
    int BalanceDays,
    double? HouseConsumptionKwhPerDay,
    double? GridImportKwhPerDay,
    decimal? ImportCostPerDay,
    IReadOnlyList<EnergyExcludedDayDto> ExcludedDays);

public sealed record EnergyCostReportDto(
    string Kind,
    DateOnly Start,
    DateOnly End,
    string Label,
    DateTimeOffset EvaluatedUntilUtc,
    bool IsRunning,
    EnergyTotalsDto Totals,
    string PreviousLabel,
    DateTimeOffset PreviousFromUtc,
    DateTimeOffset PreviousToUtc,
    EnergyTotalsDto Previous,
    EnergyComparisonDto Comparison,
    EnergyAveragesDto Averages,
    string SeriesSize,
    IReadOnlyList<EnergyBucketDto> Series,
    string CostSize,
    IReadOnlyList<EnergyBucketDto> CostBuckets,
    IReadOnlyList<string> Notes,
    IReadOnlyList<TariffPeriodDto> Tariffs)
{
    /// <summary>Base fee or feed-in compensation is not known for the whole period: the page offers the tariff editor.</summary>
    public bool TariffsIncomplete { get; init; }
}

public sealed record EnergyQuarterHourDto(
    DateTimeOffset StartUtc,
    string LocalTime,
    double GridImportKwh,
    double GridExportKwh,
    double? HouseConsumptionKwh,
    decimal? PricePerKwh,
    decimal? ImportCost,
    double GridCoveragePercent,
    double BalanceCoveragePercent);

public sealed record EnergyDayDto(DateOnly Day, IReadOnlyList<EnergyQuarterHourDto> QuarterHours);

/// <summary>Rounds for display and formats period labels in German; no calculation happens here.</summary>
public static class EnergyCostDtoMapper
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static TariffPeriodDto Map(TariffPeriod period) => new(period.ValidFrom, period.Supplier, period.MonthlyBaseFee, period.FeedInPricePerKwh);

    public static EnergyCostReportDto Map(EnergyCostReport report, IReadOnlyList<TariffPeriod> tariffs, TimeZoneInfo zone) => new(
        report.Period.Kind.ToString(),
        report.Period.Start,
        report.Period.EndExclusive.AddDays(-1),
        Label(report.Period),
        report.EvaluatedUntilUtc,
        report.EvaluatedUntilUtc < report.ToUtc,
        Map(report.Totals),
        Label(report.PreviousPeriod),
        report.PreviousFromUtc,
        report.PreviousToUtc,
        Map(report.Previous),
        new EnergyComparisonDto(Map(report.Comparison.HouseConsumptionKwh), Map(report.Comparison.GridImportKwh), Map(report.Comparison.GridExportKwh),
            Map(report.Comparison.ImportCost), Map(report.Comparison.TotalAfterCompensation), report.Comparison.Problem),
        new EnergyAveragesDto(report.Averages.CompleteDays, report.Averages.GridDays, report.Averages.BalanceDays,
            Round(report.Averages.HouseConsumptionKwhPerDay, 2), Round(report.Averages.GridImportKwhPerDay, 2),
            report.Averages.ImportCostPerDay is { } perDay ? Math.Round(perDay, 2) : null,
            report.Averages.ExcludedDays.Select(day => new EnergyExcludedDayDto(day.Day, day.Reason)).ToArray()),
        report.SeriesSize.ToString(),
        report.Series.Select(bucket => Map(bucket, report.SeriesSize, zone)).ToArray(),
        report.CostSize.ToString(),
        report.CostBuckets.Select(bucket => Map(bucket, report.CostSize, zone)).ToArray(),
        report.Notes,
        tariffs.Select(Map).ToArray())
    {
        TariffsIncomplete = tariffs.Count == 0 || !report.Totals.BaseFeeComplete || !report.Totals.FeedInComplete
    };

    public static string Label(EnergyPeriod period) => period.Kind switch
    {
        EnergyPeriodKind.Day => period.Start.ToString("dddd, d. MMMM yyyy", German),
        EnergyPeriodKind.Week => $"KW {ISOWeek.GetWeekOfYear(period.Start.ToDateTime(TimeOnly.MinValue))} ({period.Start.ToString("d.M.", German)}–{period.EndExclusive.AddDays(-1).ToString("d.M.yyyy", German)})",
        EnergyPeriodKind.Month => period.Start.ToString("MMMM yyyy", German),
        EnergyPeriodKind.Quarter => $"{(period.Start.Month - 1) / 3 + 1}. Quartal {period.Start.Year}",
        EnergyPeriodKind.Year => period.Start.Year.ToString(CultureInfo.InvariantCulture),
        _ => $"{period.Start.ToString("d.M.yyyy", German)} – {period.EndExclusive.AddDays(-1).ToString("d.M.yyyy", German)}"
    };

    private static EnergyBucketDto Map(EnergyBucket bucket, EnergyBucketSize size, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(bucket.StartUtc, zone);
        var label = size switch
        {
            EnergyBucketSize.QuarterHour or EnergyBucketSize.Hour => local.ToString("HH:mm", German),
            EnergyBucketSize.Day => local.ToString("ddd d.M.", German),
            EnergyBucketSize.Month => local.ToString("MMM yyyy", German),
            _ => $"Q{(local.Month - 1) / 3 + 1} {local.Year}"
        };
        if (size == EnergyBucketSize.Hour && (bucket.EndUtc - bucket.StartUtc).TotalHours <= 1 && local.Hour == 0)
            label = local.ToString("ddd HH:mm", German);
        return new EnergyBucketDto(bucket.StartUtc, bucket.EndUtc, label, Map(bucket.Totals));
    }

    private static EnergyTotalsDto Map(EnergyTotals totals) => new(
        Math.Round(totals.EvaluatedHours, 2),
        Math.Round(totals.GridCoverage * 100, 1),
        Math.Round(totals.BalanceCoverage * 100, 1),
        Round(totals.HouseConsumptionKwh, 3),
        Round(totals.GridImportKwh, 3),
        Round(totals.GridExportKwh, 3),
        Round(totals.PvProductionKwh, 3),
        Round(totals.PrimaryBatteryChargeKwh, 3),
        Round(totals.PrimaryBatteryDischargeKwh, 3),
        Round(totals.SupplementaryBatteryChargeKwh, 3),
        Round(totals.SupplementaryBatteryDischargeKwh, 3),
        Round(totals.EvChargingKwh, 3),
        totals.ImportCost is { } cost ? Math.Round(cost, 2) : null,
        Math.Round(totals.UnpricedImportKwh, 3),
        totals.WeightedPricePerKwh is { } price ? Math.Round(price, 4) : null,
        totals.BaseFee is { } fee ? Math.Round(fee, 2) : null,
        totals.BaseFeeComplete,
        totals.FeedInCompensation is { } feedIn ? Math.Round(feedIn, 2) : null,
        totals.FeedInComplete,
        totals.TotalAfterCompensation is { } total ? Math.Round(total, 2) : null,
        totals.ImportCostComplete,
        totals.TotalComplete);

    private static EnergyChangeDto Map(EnergyChange change) => new(Round(change.Absolute, 3), Round(change.Percent, 1));

    private static double? Round(double? value, int digits) => value is { } v ? Math.Round(v, digits) : null;
}
