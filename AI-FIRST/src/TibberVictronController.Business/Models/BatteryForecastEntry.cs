namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents one calculated Decision Engine forecast entry for a single 15-minute slot.
/// </summary>
public sealed record BatteryForecastEntry(
    ForecastTimeSlot TimeSlot,
    decimal TibberPricePerKwh,
    string TibberPriceCurrency,
    decimal ExpectedPvYieldKwh,
    decimal ExpectedConsumptionKwh,
    decimal ExpectedGridImportBeforeBatteryKwh,
    decimal StateOfChargeBeforePercent,
    decimal StateOfChargeAfterPercent,
    CurrentBatteryDecision Decision,
    IReadOnlyList<BatteryDecisionReason> Reasons);
