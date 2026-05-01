namespace TibberVictronController.Business.Models;

/// <summary>
/// Provides readable assignment-based input for creating a daily battery savings summary.
/// </summary>
public sealed class BatterySavingsDailySummaryValues
{
    public DateOnly AccountingDate { get; init; }

    public string Currency { get; init; } = string.Empty;

    public decimal GridChargedEnergyKwh { get; init; }

    public decimal GridChargeCost { get; init; }

    public decimal PvChargedEnergyKwh { get; init; }

    public decimal PvOpportunityCost { get; init; }

    public decimal DischargedEnergyKwh { get; init; }

    public decimal DischargeAvoidedCost { get; init; }

    public decimal NetSavings { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }
}
