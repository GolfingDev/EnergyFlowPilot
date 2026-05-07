namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Contains stable rule identifiers used in Decision Engine forecast explanations and audit exports.
/// </summary>
public static class BatteryForecastRuleIds
{
    public const string PvSurplusCharge = "PV_SURPLUS_CHARGE";
    public const string BatteryFullPvSurplus = "BATTERY_FULL_PV_SURPLUS";
    public const string PreserveHeadroomForNegativePrice = "PRESERVE_HEADROOM_FOR_NEGATIVE_PRICE";
    public const string NegativePriceGridCharge = "NEGATIVE_PRICE_GRID_CHARGE";
    public const string PlannedGridCharge = "PLANNED_GRID_CHARGE";
    public const string DischargeBeforeNegativePriceWindow = "DISCHARGE_BEFORE_NEGATIVE_PRICE_WINDOW";
    public const string DischargeForFuturePvHeadroom = "DISCHARGE_FOR_FUTURE_PV_HEADROOM";
    public const string ExpensivePriceDischarge = "EXPENSIVE_PRICE_DISCHARGE";
    public const string MinimumSocReserve = "MIN_SOC_RESERVE";
    public const string EndSocReserve = "END_SOC_RESERVE";
    public const string PlanningMaximumGridChargeLimit = "PLANNING_MAX_SOC_GRID_CHARGE_LIMIT";
    public const string PlanningMaximumSocHeadroom = "PLANNING_MAX_SOC_HEADROOM";
    public const string WaitForNegativePriceWindow = "WAIT_FOR_NEGATIVE_PRICE_WINDOW";
    public const string BatteryFullIdle = "BATTERY_FULL_IDLE";
    public const string NeutralIdle = "NEUTRAL_IDLE";
}
