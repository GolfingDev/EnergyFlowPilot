namespace TibberVictronController.Business.Models;

/// <summary>
/// Stores one Europe/Berlin reporting day of monetary battery savings.
/// </summary>
public sealed record BatterySavingsDailySummary
{
    /// <summary>
    /// Validates daily accounting values before they are persisted or aggregated.
    /// </summary>
    public BatterySavingsDailySummary(BatterySavingsDailySummaryValues values)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values), "Die Tageswerte fuer die Batterie-Ersparnis duerfen nicht null sein.");
        }

        if (string.IsNullOrWhiteSpace(values.Currency))
        {
            throw new ArgumentException("Die Waehrung der Batterie-Ersparnis muss angegeben werden.", nameof(values));
        }

        if (values.UpdatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Aktualisierungszeitpunkt der Batterie-Ersparnis muss in UTC angegeben sein.", nameof(values));
        }

        ValidateNonNegative(values.GridChargedEnergyKwh, nameof(values.GridChargedEnergyKwh));
        ValidateNonNegative(values.PvChargedEnergyKwh, nameof(values.PvChargedEnergyKwh));
        ValidateNonNegative(values.DischargedEnergyKwh, nameof(values.DischargedEnergyKwh));

        AccountingDate = values.AccountingDate;
        Currency = values.Currency;
        GridChargedEnergyKwh = Round(values.GridChargedEnergyKwh);
        GridChargeCost = Round(values.GridChargeCost);
        PvChargedEnergyKwh = Round(values.PvChargedEnergyKwh);
        PvOpportunityCost = Round(values.PvOpportunityCost);
        DischargedEnergyKwh = Round(values.DischargedEnergyKwh);
        DischargeAvoidedCost = Round(values.DischargeAvoidedCost);
        NetSavings = Round(values.NetSavings);
        UpdatedAtUtc = values.UpdatedAtUtc;
    }

    public DateOnly AccountingDate { get; }

    public string Currency { get; }

    public decimal GridChargedEnergyKwh { get; }

    public decimal GridChargeCost { get; }

    public decimal PvChargedEnergyKwh { get; }

    public decimal PvOpportunityCost { get; }

    public decimal DischargedEnergyKwh { get; }

    public decimal DischargeAvoidedCost { get; }

    public decimal NetSavings { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public decimal? AverageGridChargePricePerKwh => CalculateAveragePrice(GridChargeCost, GridChargedEnergyKwh);

    public decimal? AveragePvOpportunityPricePerKwh => CalculateAveragePrice(PvOpportunityCost, PvChargedEnergyKwh);

    public decimal? AverageDischargePricePerKwh => CalculateAveragePrice(DischargeAvoidedCost, DischargedEnergyKwh);

    private static void ValidateNonNegative(decimal value, string valueName)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(valueName, "Energie-Werte fuer die Batterie-Ersparnis duerfen nicht negativ sein.");
        }
    }

    private static decimal Round(decimal value)
    {
        return Math.Round(value, 4, MidpointRounding.AwayFromZero);
    }

    private static decimal? CalculateAveragePrice(decimal cost, decimal energyKwh)
    {
        if (energyKwh <= 0m)
        {
            return null;
        }

        return Round(cost / energyKwh);
    }
}
