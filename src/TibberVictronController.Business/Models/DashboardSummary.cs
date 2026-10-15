namespace TibberVictronController.Business.Models;

/// <summary>
/// Categories that the dashboard uses for live state, plan slots and plan segments; one colour per category in every view.
/// </summary>
public enum DashboardActionCategory
{
    Discharge,
    PvCharge,
    GridCharge,
    Hold,
    Full,
    Idle
}

public enum DashboardCheckSeverity
{
    Ok,
    Info,
    Warning,
    Critical
}

/// <summary>
/// Contains everything the control-room dashboard shows for the current moment and the next hours.
/// </summary>
public sealed record DashboardSummary(
    DateTimeOffset CreatedAtUtc,
    DashboardCurrentState Current,
    DashboardLiveState? Live,
    IReadOnlyList<DashboardCheck> Checks,
    IReadOnlyList<DashboardPlanSlot> PlanSlots,
    IReadOnlyList<DashboardPlanSegment> PlanSegments);

/// <summary>
/// Describes the latest direct decision in dashboard terms.
/// </summary>
public sealed record DashboardCurrentState(
    DashboardActionCategory Category,
    int TargetPowerWatts,
    string? RuleId,
    string? Reason,
    decimal? PricePerKwh,
    string? Currency,
    DateTimeOffset? DecidedAtUtc);

/// <summary>
/// Measured power values of both storage systems; positive battery values mean charging, positive grid values mean import.
/// </summary>
public sealed record DashboardLiveState(
    DateTimeOffset MeasuredAtUtc,
    decimal? GridPowerWatts,
    decimal HouseConsumptionWatts,
    decimal? BatteryPowerWatts,
    decimal? BatterySocPercent,
    decimal? SupplementaryPvProductionWatts,
    decimal? SupplementaryBatteryPowerWatts,
    decimal? SupplementaryBatterySocPercent);

/// <summary>
/// A plain-language check with a stable identifier, e.g. whether the battery feeds into the grid.
/// </summary>
public sealed record DashboardCheck(
    string CheckId,
    DashboardCheckSeverity Severity,
    string Message);

public sealed record DashboardPlanSlot(
    ForecastTimeSlot TimeSlot,
    decimal PricePerKwh,
    decimal StateOfChargeAfterPercent,
    decimal ExpectedPvYieldKwh,
    decimal ExpectedConsumptionKwh,
    DashboardActionCategory Category);

/// <summary>
/// Consecutive plan slots with the same action, summarised with the values needed to explain the step.
/// </summary>
public sealed record DashboardPlanSegment(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    DashboardActionCategory Category,
    decimal StateOfChargeStartPercent,
    decimal StateOfChargeEndPercent,
    decimal MinimumPricePerKwh,
    decimal MaximumPricePerKwh,
    decimal ExpectedPvYieldKwh,
    decimal GridEnergyKwh,
    string? RuleId,
    string? Reason);

/// <summary>
/// Measured review of a past period with the quality figures that matter for a site with two storage systems.
/// </summary>
public sealed record DashboardReview(
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
    IReadOnlyList<DashboardReviewHour> Hours);

public sealed record DashboardReviewHour(
    DateTimeOffset StartsAtUtc,
    decimal? BatterySocEndPercent,
    decimal GridImportKwh,
    decimal GridExportKwh,
    decimal BatteryChargeKwh,
    decimal BatteryDischargeKwh,
    decimal BatteryFeedInKwh,
    decimal SupplementaryChargedFromBatteryKwh);
