using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Auditing;

/// <summary>
/// Contains all deterministic inputs needed to challenge a full-day Decision Engine forecast.
/// </summary>
public sealed record DecisionAuditScenario(
    string Name,
    BatteryState InitialBatteryState,
    BatteryConfiguration BatteryConfiguration,
    IReadOnlyList<TibberPriceForecastSlot> PriceForecast,
    IReadOnlyList<PvYieldForecastSlot> PvForecast,
    IReadOnlyList<ConsumptionForecastSlot> ConsumptionForecast,
    decimal FeedInCompensationPricePerKwh);
