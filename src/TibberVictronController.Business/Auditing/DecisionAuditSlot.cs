using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Auditing;

/// <summary>
/// Represents one readable audit row for a forecast decision slot.
/// </summary>
public sealed record DecisionAuditSlot(
    ForecastTimeSlot TimeSlot,
    string Action,
    string RuleId,
    string Reason,
    string AlternativeAction,
    string AlternativeRejectedReason,
    decimal TibberPricePerKwh,
    decimal ExpectedPvYieldKwh,
    decimal ExpectedConsumptionKwh,
    decimal GridImportKwh,
    decimal GridExportKwh,
    decimal ChargedEnergyKwh,
    decimal DischargedEnergyKwh,
    decimal StateOfChargeBeforePercent,
    decimal ExpectedSocPercent,
    int TargetPowerWatts,
    IReadOnlyList<string> ConstraintFlags);
