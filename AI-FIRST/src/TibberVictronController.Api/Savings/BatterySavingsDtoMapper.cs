using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Savings;

public static class BatterySavingsDtoMapper
{
    public static BatterySavingsResponseDto Map(BatterySavingsDtoMappingInput input)
    {
        return new BatterySavingsResponseDto
        {
            Period = input.Period,
            ReferenceDate = input.ReferenceDate,
            StartDate = input.Query.StartDate,
            EndDate = input.Query.EndDate,
            Currency = input.Query.Currency,
            Aggregate = MapMetrics(input.Aggregate),
            DailySummaries = input.DailySummaries
                .Select(MapDailySummary)
                .ToArray()
        };
    }

    private static BatterySavingsDailySummaryDto MapDailySummary(BatterySavingsDailySummary summary)
    {
        var metrics = MapMetrics(summary);

        return new BatterySavingsDailySummaryDto
        {
            AccountingDate = summary.AccountingDate,
            GridChargedEnergyKwh = metrics.GridChargedEnergyKwh,
            GridChargeCost = metrics.GridChargeCost,
            PvChargedEnergyKwh = metrics.PvChargedEnergyKwh,
            PvOpportunityCost = metrics.PvOpportunityCost,
            DischargedEnergyKwh = metrics.DischargedEnergyKwh,
            DischargeAvoidedCost = metrics.DischargeAvoidedCost,
            NetSavings = metrics.NetSavings,
            AverageGridChargePricePerKwh = metrics.AverageGridChargePricePerKwh,
            AveragePvOpportunityPricePerKwh = metrics.AveragePvOpportunityPricePerKwh,
            AverageDischargePricePerKwh = metrics.AverageDischargePricePerKwh
        };
    }

    private static BatterySavingsMetricsDto MapMetrics(BatterySavingsDailySummary summary)
    {
        return new BatterySavingsMetricsDto
        {
            GridChargedEnergyKwh = summary.GridChargedEnergyKwh,
            GridChargeCost = summary.GridChargeCost,
            PvChargedEnergyKwh = summary.PvChargedEnergyKwh,
            PvOpportunityCost = summary.PvOpportunityCost,
            DischargedEnergyKwh = summary.DischargedEnergyKwh,
            DischargeAvoidedCost = summary.DischargeAvoidedCost,
            NetSavings = summary.NetSavings,
            AverageGridChargePricePerKwh = summary.AverageGridChargePricePerKwh,
            AveragePvOpportunityPricePerKwh = summary.AveragePvOpportunityPricePerKwh,
            AverageDischargePricePerKwh = summary.AverageDischargePricePerKwh
        };
    }

    private static BatterySavingsMetricsDto MapMetrics(BatterySavingsAggregate aggregate)
    {
        return new BatterySavingsMetricsDto
        {
            GridChargedEnergyKwh = aggregate.GridChargedEnergyKwh,
            GridChargeCost = aggregate.GridChargeCost,
            PvChargedEnergyKwh = aggregate.PvChargedEnergyKwh,
            PvOpportunityCost = aggregate.PvOpportunityCost,
            DischargedEnergyKwh = aggregate.DischargedEnergyKwh,
            DischargeAvoidedCost = aggregate.DischargeAvoidedCost,
            NetSavings = aggregate.NetSavings,
            AverageGridChargePricePerKwh = aggregate.AverageGridChargePricePerKwh,
            AveragePvOpportunityPricePerKwh = aggregate.AveragePvOpportunityPricePerKwh,
            AverageDischargePricePerKwh = aggregate.AverageDischargePricePerKwh
        };
    }
}

public sealed class BatterySavingsDtoMappingInput
{
    public string Period { get; init; } = string.Empty;

    public DateOnly? ReferenceDate { get; init; }

    public BatterySavingsQuery Query { get; init; } = new();

    public IReadOnlyList<BatterySavingsDailySummary> DailySummaries { get; init; } = Array.Empty<BatterySavingsDailySummary>();

    public BatterySavingsAggregate Aggregate { get; init; } = BatterySavingsAggregate.FromDailySummaries(Array.Empty<BatterySavingsDailySummary>());
}
