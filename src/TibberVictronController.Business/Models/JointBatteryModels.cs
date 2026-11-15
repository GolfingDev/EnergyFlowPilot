namespace TibberVictronController.Business.Models;

public sealed record JointBatteryUnit(string Id, BatteryConfiguration Configuration, BatteryState State, int MeasuredPowerWatts,
    int? AvailableChargeWatts = null, int? AvailableDischargeWatts = null);
/// <summary>EnergyValuePerKwh: what one more kWh stored after this slot saves later (marginal value from the plan's cost-to-go).</summary>
public sealed record JointBatteryTarget(string Id, int SignedPowerWatts, decimal SocBeforePercent, decimal SocAfterPercent, decimal EnergyValuePerKwh = 0);
public sealed record JointBatterySlot(ForecastTimeSlot TimeSlot, decimal PricePerKwh, string Currency,
    bool IsPriceEstimated, BatteryChargeSource? ChargeSource, IReadOnlyList<JointBatteryTarget> Targets, string Reason,
    bool TargetLimited, decimal ExpectedPvKwh = 0, decimal ExpectedConsumptionKwh = 0)
{
    /// <summary>
    /// Set by the planner only: the planned discharge covers the whole net demand the planner assumed for this quarter
    /// hour, i.e. the decision is "cover the house demand", not a fixed number of watts. The execution may then raise
    /// the discharge of the batteries the plan discharges up to the measured demand. False for partial discharge
    /// (demand left to the grid on purpose or because of a limit), hold and charge.
    /// </summary>
    public bool FollowsDemand { get; init; }
}
public sealed record JointBatteryPlan(DateTimeOffset CalculatedAtUtc, IReadOnlyList<JointBatteryUnit> Batteries,
    IReadOnlyList<JointBatterySlot> Slots, int MaximumGridImportWatts, IReadOnlyList<string> Warnings)
{
    public string? PriceSource { get; init; }
}
public sealed record HistoricalPriceRequest(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string Source, string Currency);
/// <summary>One shared, demand-capped value for deliverable energy beyond the published price horizon.</summary>
public sealed record JointBatteryContinuation(decimal ValuePerDeliveredKwh, decimal MaximumDeliveredEnergyKwh,
    decimal HistoricalTypicalPricePerKwh, int HistoricalDays);
public sealed class JointBatteryPlanInput
{
    public required DateTimeOffset NowUtc { get; init; }
    public required IReadOnlyList<JointBatteryUnit> Batteries { get; init; }
    public required IReadOnlyList<TibberPriceForecastSlot> Prices { get; init; }
    public required IReadOnlyList<PvYieldForecastSlot> Pv { get; init; }
    public required IReadOnlyList<ConsumptionForecastSlot> Consumption { get; init; }
    public required int MaximumGridImportWatts { get; init; }
    public decimal FeedInCompensationPerKwh { get; init; }
    public JointBatteryContinuation? Continuation { get; init; }
}
