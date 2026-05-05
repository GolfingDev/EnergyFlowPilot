namespace TibberVictronController.Dal.Entities;

public sealed class BatterySavingsDailySummaryEntity
{
    public DateOnly AccountingDate { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal GridChargedEnergyKwh { get; set; }

    public decimal GridChargeCost { get; set; }

    public decimal PvChargedEnergyKwh { get; set; }

    public decimal PvOpportunityCost { get; set; }

    public decimal DischargedEnergyKwh { get; set; }

    public decimal DischargeAvoidedCost { get; set; }

    public decimal NetSavings { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
