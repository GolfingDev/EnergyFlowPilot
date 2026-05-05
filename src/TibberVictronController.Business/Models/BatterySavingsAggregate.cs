namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents a summed savings view across daily battery savings summaries.
/// </summary>
public sealed record BatterySavingsAggregate
{
    private BatterySavingsAggregate(BatterySavingsDailySummaryValues values)
    {
        var summary = new BatterySavingsDailySummary(values);

        Currency = summary.Currency;
        GridChargedEnergyKwh = summary.GridChargedEnergyKwh;
        GridChargeCost = summary.GridChargeCost;
        PvChargedEnergyKwh = summary.PvChargedEnergyKwh;
        PvOpportunityCost = summary.PvOpportunityCost;
        DischargedEnergyKwh = summary.DischargedEnergyKwh;
        DischargeAvoidedCost = summary.DischargeAvoidedCost;
        NetSavings = summary.NetSavings;
        AverageGridChargePricePerKwh = summary.AverageGridChargePricePerKwh;
        AveragePvOpportunityPricePerKwh = summary.AveragePvOpportunityPricePerKwh;
        AverageDischargePricePerKwh = summary.AverageDischargePricePerKwh;
    }

    public string Currency { get; }

    public decimal GridChargedEnergyKwh { get; }

    public decimal GridChargeCost { get; }

    public decimal PvChargedEnergyKwh { get; }

    public decimal PvOpportunityCost { get; }

    public decimal DischargedEnergyKwh { get; }

    public decimal DischargeAvoidedCost { get; }

    public decimal NetSavings { get; }

    public decimal? AverageGridChargePricePerKwh { get; }

    public decimal? AveragePvOpportunityPricePerKwh { get; }

    public decimal? AverageDischargePricePerKwh { get; }

    /// <summary>
    /// Sums daily values and derives weighted average prices from the summed energy and money values.
    /// </summary>
    public static BatterySavingsAggregate FromDailySummaries(IReadOnlyList<BatterySavingsDailySummary> summaries)
    {
        if (summaries is null)
        {
            throw new ArgumentNullException(nameof(summaries), "Die Batterie-Ersparnis-Tageswerte duerfen nicht null sein.");
        }

        if (summaries.Count == 0)
        {
            return new BatterySavingsAggregate(new BatterySavingsDailySummaryValues
            {
                AccountingDate = DateOnly.MinValue,
                Currency = "EUR",
                UpdatedAtUtc = DateTimeOffset.UnixEpoch
            });
        }

        var currency = summaries[0].Currency;

        if (summaries.Any(summary => !string.Equals(summary.Currency, currency, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Batterie-Ersparnis kann nur fuer eine einheitliche Waehrung aggregiert werden.", nameof(summaries));
        }

        var values = new BatterySavingsDailySummaryValues
        {
            AccountingDate = summaries.Min(summary => summary.AccountingDate),
            Currency = currency,
            GridChargedEnergyKwh = summaries.Sum(summary => summary.GridChargedEnergyKwh),
            GridChargeCost = summaries.Sum(summary => summary.GridChargeCost),
            PvChargedEnergyKwh = summaries.Sum(summary => summary.PvChargedEnergyKwh),
            PvOpportunityCost = summaries.Sum(summary => summary.PvOpportunityCost),
            DischargedEnergyKwh = summaries.Sum(summary => summary.DischargedEnergyKwh),
            DischargeAvoidedCost = summaries.Sum(summary => summary.DischargeAvoidedCost),
            NetSavings = summaries.Sum(summary => summary.NetSavings),
            UpdatedAtUtc = summaries.Max(summary => summary.UpdatedAtUtc)
        };

        return new BatterySavingsAggregate(values);
    }
}
