using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Dashboard;

/// <summary>
/// Maps the control-room dashboard endpoints; all logic lives in the dashboard summary service.
/// </summary>
public static class DashboardSummaryEndpoints
{
    private const int MaximumReviewHours = 24 * 7;

    public static IEndpointRouteBuilder MapDashboardSummaryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/dashboard/summary", GetSummaryAsync)
            .WithName("GetDashboardSummary")
            .WithTags("Dashboard");

        endpoints.MapGet("/api/dashboard/review", GetReviewAsync)
            .WithName("GetDashboardReview")
            .WithTags("Dashboard");

        return endpoints;
    }

    public static async Task<IResult> GetSummaryAsync(
        IDashboardSummaryService dashboardSummaryService,
        CancellationToken cancellationToken)
    {
        var summary = await dashboardSummaryService.GetSummaryAsync(cancellationToken);

        return TypedResults.Ok(DashboardSummaryDtoMapper.Map(summary));
    }

    public static async Task<IResult> GetReviewAsync(
        IDashboardSummaryService dashboardSummaryService,
        int? hours,
        CancellationToken cancellationToken)
    {
        var effectiveHours = Math.Clamp(hours ?? 24, 1, MaximumReviewHours);
        var endsAtUtc = DateTimeOffset.UtcNow;
        var review = await dashboardSummaryService.GetReviewAsync(
            endsAtUtc.AddHours(-effectiveHours),
            endsAtUtc,
            cancellationToken);

        return TypedResults.Ok(DashboardSummaryDtoMapper.Map(review));
    }
}

/// <summary>
/// Maps dashboard domain models to frontend DTOs; categories and severities become camelCase strings.
/// </summary>
public static class DashboardSummaryDtoMapper
{
    public static DashboardSummaryDto Map(DashboardSummary summary)
    {
        return new DashboardSummaryDto(
            summary.CreatedAtUtc,
            new DashboardCurrentStateDto(
                ToName(summary.Current.Category),
                summary.Current.TargetPowerWatts,
                summary.Current.RuleId,
                summary.Current.Reason,
                summary.Current.PricePerKwh,
                summary.Current.Currency,
                summary.Current.DecidedAtUtc),
            summary.Live is null
                ? null
                : new DashboardLiveStateDto(
                    summary.Live.MeasuredAtUtc,
                    summary.Live.GridPowerWatts,
                    summary.Live.HouseConsumptionWatts,
                    summary.Live.BatteryPowerWatts,
                    summary.Live.BatterySocPercent,
                    summary.Live.SupplementaryPvProductionWatts,
                    summary.Live.SupplementaryBatteryPowerWatts,
                    summary.Live.SupplementaryBatterySocPercent),
            summary.Checks.Select(check => new DashboardCheckDto(check.CheckId, ToName(check.Severity), check.Message)).ToArray(),
            summary.PlanSlots.Select(slot => new DashboardPlanSlotDto(
                slot.TimeSlot.StartsAtUtc,
                slot.TimeSlot.EndsAtUtc,
                slot.PricePerKwh,
                slot.StateOfChargeAfterPercent,
                slot.ExpectedPvYieldKwh,
                slot.ExpectedConsumptionKwh,
                ToName(slot.Category),
                slot.IsPriceEstimated)).ToArray(),
            summary.PlanSegments.Select(segment => new DashboardPlanSegmentDto(
                segment.StartsAtUtc,
                segment.EndsAtUtc,
                ToName(segment.Category),
                segment.StateOfChargeStartPercent,
                segment.StateOfChargeEndPercent,
                segment.MinimumPricePerKwh,
                segment.MaximumPricePerKwh,
                segment.ExpectedPvYieldKwh,
                segment.GridEnergyKwh,
                segment.RuleId,
                segment.Reason)).ToArray()) {
                PriceSource = summary.PriceSource, SupplierName = summary.SupplierName,
                Overview = summary.Overview is not { } overview ? null : new DashboardDecisionOverviewDto(
                    ToName(overview.Category), overview.Reason, overview.PricePerKwh, overview.Currency,
                    overview.Next is not { } next ? null : new DashboardNextDecisionDto(next.StartsAtUtc, ToName(next.Category),
                        next.Reason, next.PricePerKwh, next.Currency, next.IsPriceEstimated))
            };
    }

    public static DashboardReviewDto Map(DashboardReview review)
    {
        return new DashboardReviewDto(
            review.StartsAtUtc,
            review.EndsAtUtc,
            review.BatteryDischargeKwh,
            review.GridImportKwh,
            review.GridImportWhileBatteryAvailableKwh,
            review.GridChargeKwh,
            review.BatteryFeedInKwh,
            review.SupplementaryChargedFromBatteryKwh,
            review.SupplementaryStateOfChargeStartPercent,
            review.SupplementaryStateOfChargeEndPercent,
            review.Hours.Select(hour => new DashboardReviewHourDto(
                hour.StartsAtUtc,
                hour.BatterySocEndPercent,
                hour.GridImportKwh,
                hour.GridExportKwh,
                hour.BatteryChargeKwh,
                hour.BatteryDischargeKwh,
                hour.BatteryFeedInKwh,
                hour.SupplementaryChargedFromBatteryKwh,
                hour.ActualCost,
                hour.BaselineCost)).ToArray(),
            review.Costs is not { } costs
                ? null
                : new DashboardCostComparisonDto(
                    costs.ActualCost,
                    costs.BaselineCost,
                    costs.WithoutBatteryCost,
                    costs.StoredEnergyDifferenceKwh,
                    costs.StoredEnergyDifferenceValue,
                    costs.AdvantageOverBaseline,
                    costs.AdvantageOverNoBattery,
                    costs.AveragePricePerKwh,
                    costs.PricedCoveragePercent));
    }

    private static string ToName<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        return JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    }
}

public sealed record DashboardSummaryDto(
    DateTimeOffset CreatedAtUtc,
    DashboardCurrentStateDto Current,
    DashboardLiveStateDto? Live,
    IReadOnlyList<DashboardCheckDto> Checks,
    IReadOnlyList<DashboardPlanSlotDto> PlanSlots,
    IReadOnlyList<DashboardPlanSegmentDto> PlanSegments)
{
    public DashboardDecisionOverviewDto? Overview { get; init; }
    public string? PriceSource { get; init; }
    public string? SupplierName { get; init; }
}
public sealed record DashboardDecisionOverviewDto(string Category, string Reason, decimal? PricePerKwh, string? Currency, DashboardNextDecisionDto? Next);
public sealed record DashboardNextDecisionDto(DateTimeOffset StartsAtUtc, string Category, string Reason, decimal PricePerKwh, string Currency, bool IsPriceEstimated);

public sealed record DashboardCurrentStateDto(
    string Category,
    int TargetPowerWatts,
    string? RuleId,
    string? Reason,
    decimal? PricePerKwh,
    string? Currency,
    DateTimeOffset? DecidedAtUtc);

public sealed record DashboardLiveStateDto(
    DateTimeOffset MeasuredAtUtc,
    decimal? GridPowerWatts,
    decimal HouseConsumptionWatts,
    decimal? BatteryPowerWatts,
    decimal? BatterySocPercent,
    decimal? SupplementaryPvProductionWatts,
    decimal? SupplementaryBatteryPowerWatts,
    decimal? SupplementaryBatterySocPercent);

public sealed record DashboardCheckDto(string CheckId, string Severity, string Message);

public sealed record DashboardPlanSlotDto(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    decimal PricePerKwh,
    decimal StateOfChargeAfterPercent,
    decimal ExpectedPvYieldKwh,
    decimal ExpectedConsumptionKwh,
    string Category,
    bool IsPriceEstimated);

public sealed record DashboardPlanSegmentDto(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    string Category,
    decimal StateOfChargeStartPercent,
    decimal StateOfChargeEndPercent,
    decimal MinimumPricePerKwh,
    decimal MaximumPricePerKwh,
    decimal ExpectedPvYieldKwh,
    decimal GridEnergyKwh,
    string? RuleId,
    string? Reason);

public sealed record DashboardReviewDto(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    decimal BatteryDischargeKwh,
    decimal GridImportKwh,
    decimal GridImportWhileBatteryAvailableKwh,
    decimal GridChargeKwh,
    decimal BatteryFeedInKwh,
    decimal SupplementaryChargedFromBatteryKwh,
    decimal? SupplementaryStateOfChargeStartPercent,
    decimal? SupplementaryStateOfChargeEndPercent,
    IReadOnlyList<DashboardReviewHourDto> Hours,
    DashboardCostComparisonDto? Costs);

public sealed record DashboardReviewHourDto(
    DateTimeOffset StartsAtUtc,
    decimal? BatterySocEndPercent,
    decimal GridImportKwh,
    decimal GridExportKwh,
    decimal BatteryChargeKwh,
    decimal BatteryDischargeKwh,
    decimal BatteryFeedInKwh,
    decimal SupplementaryChargedFromBatteryKwh,
    decimal? ActualCost,
    decimal? BaselineCost);

public sealed record DashboardCostComparisonDto(
    decimal ActualCost,
    decimal BaselineCost,
    decimal WithoutBatteryCost,
    decimal StoredEnergyDifferenceKwh,
    decimal StoredEnergyDifferenceValue,
    decimal AdvantageOverBaseline,
    decimal AdvantageOverNoBattery,
    decimal AveragePricePerKwh,
    decimal PricedCoveragePercent);
