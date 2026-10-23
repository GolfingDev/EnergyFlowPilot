namespace TibberVictronController.Business.Models;

public sealed record JointBatteryUnit(string Id, BatteryConfiguration Configuration, BatteryState State, int MeasuredPowerWatts,
    int? AvailableChargeWatts = null, int? AvailableDischargeWatts = null);
public sealed record JointBatteryTarget(string Id, int SignedPowerWatts, decimal SocBeforePercent, decimal SocAfterPercent);
public sealed record JointBatterySlot(ForecastTimeSlot TimeSlot, decimal PricePerKwh, string Currency,
    bool IsPriceEstimated, BatteryChargeSource? ChargeSource, IReadOnlyList<JointBatteryTarget> Targets, string Reason,
    bool TargetLimited, decimal ExpectedPvKwh = 0, decimal ExpectedConsumptionKwh = 0);
public sealed record JointBatteryPlan(DateTimeOffset CalculatedAtUtc, IReadOnlyList<JointBatteryUnit> Batteries,
    IReadOnlyList<JointBatterySlot> Slots, int MaximumGridImportWatts, IReadOnlyList<string> Warnings);
public sealed class JointBatteryPlanInput
{
    public required DateTimeOffset NowUtc { get; init; }
    public required IReadOnlyList<JointBatteryUnit> Batteries { get; init; }
    public required IReadOnlyList<TibberPriceForecastSlot> Prices { get; init; }
    public required IReadOnlyList<PvYieldForecastSlot> Pv { get; init; }
    public required IReadOnlyList<ConsumptionForecastSlot> Consumption { get; init; }
    public required int MaximumGridImportWatts { get; init; }
    public decimal FeedInCompensationPerKwh { get; init; }
}
