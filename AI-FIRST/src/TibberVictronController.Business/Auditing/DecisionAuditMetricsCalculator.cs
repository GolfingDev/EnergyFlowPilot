namespace TibberVictronController.Business.Auditing;

/// <summary>
/// Calculates audit metrics from readable decision slots.
/// </summary>
public static class DecisionAuditMetricsCalculator
{
    /// <summary>
    /// Aggregates economic and technical metrics for one decision sequence.
    /// </summary>
    public static DecisionAuditMetrics Calculate(
        IReadOnlyList<DecisionAuditSlot> decisionSlots,
        DecisionAuditScenario scenario,
        decimal? baselineCost)
    {
        if (decisionSlots.Count == 0)
        {
            return DecisionAuditMetrics.Empty;
        }

        var gridImportKwh = decisionSlots.Sum(slot => slot.GridImportKwh);
        var gridExportKwh = decisionSlots.Sum(slot => slot.GridExportKwh);
        var chargedEnergyKwh = decisionSlots.Sum(slot => slot.ChargedEnergyKwh);
        var gridChargedEnergyKwh = decisionSlots
            .Where(slot => slot.Action == "ChargeFromGrid")
            .Sum(slot => slot.ChargedEnergyKwh);
        var pvChargedEnergyKwh = decisionSlots
            .Where(slot => slot.Action == "ChargeFromPV")
            .Sum(slot => slot.ChargedEnergyKwh);
        var dischargedEnergyKwh = decisionSlots.Sum(slot => slot.DischargedEnergyKwh);
        var totalPvYieldKwh = decisionSlots.Sum(slot => slot.ExpectedPvYieldKwh);
        var totalCost = decisionSlots.Sum(slot => slot.GridImportKwh * slot.TibberPricePerKwh) -
            gridExportKwh * scenario.FeedInCompensationPricePerKwh;
        var efficiencyLossKwh = chargedEnergyKwh *
            (1m - scenario.BatteryConfiguration.RoundTripEfficiencyPercent / 100m);
        var averageImportPrice = gridImportKwh > 0m
            ? decisionSlots.Sum(slot => slot.GridImportKwh * slot.TibberPricePerKwh) / gridImportKwh
            : 0m;
        var efficiencyLossCost = efficiencyLossKwh * Math.Max(0m, averageImportPrice);
        var netBenefitAfterEfficiencyLoss = baselineCost is null
            ? 0m
            : baselineCost.Value - totalCost - efficiencyLossCost;

        return new DecisionAuditMetrics(
            TotalCost: Math.Round(totalCost, 4, MidpointRounding.AwayFromZero),
            GridImportKwh: Math.Round(gridImportKwh, 4, MidpointRounding.AwayFromZero),
            GridExportKwh: Math.Round(gridExportKwh, 4, MidpointRounding.AwayFromZero),
            PvUtilizationPercent: CalculatePvUtilizationPercent(totalPvYieldKwh, gridExportKwh),
            ChargedEnergyKwh: Math.Round(chargedEnergyKwh, 4, MidpointRounding.AwayFromZero),
            GridChargedEnergyKwh: Math.Round(gridChargedEnergyKwh, 4, MidpointRounding.AwayFromZero),
            PvChargedEnergyKwh: Math.Round(pvChargedEnergyKwh, 4, MidpointRounding.AwayFromZero),
            DischargedEnergyKwh: Math.Round(dischargedEnergyKwh, 4, MidpointRounding.AwayFromZero),
            CycleCount: Math.Round(Math.Min(chargedEnergyKwh, dischargedEnergyKwh) / scenario.BatteryConfiguration.TotalCapacityKwh, 4, MidpointRounding.AwayFromZero),
            MinimumStateOfChargePercent: decisionSlots.Min(slot => slot.ExpectedSocPercent),
            MaximumStateOfChargePercent: decisionSlots.Max(slot => slot.ExpectedSocPercent),
            EfficiencyLossKwh: Math.Round(efficiencyLossKwh, 4, MidpointRounding.AwayFromZero),
            EfficiencyLossCost: Math.Round(efficiencyLossCost, 4, MidpointRounding.AwayFromZero),
            NetBenefitAfterEfficiencyLoss: Math.Round(netBenefitAfterEfficiencyLoss, 4, MidpointRounding.AwayFromZero));
    }

    private static decimal CalculatePvUtilizationPercent(decimal totalPvYieldKwh, decimal gridExportKwh)
    {
        if (totalPvYieldKwh <= 0m)
        {
            return 0m;
        }

        return Math.Round(
            Math.Clamp((totalPvYieldKwh - gridExportKwh) / totalPvYieldKwh * 100m, 0m, 100m),
            4,
            MidpointRounding.AwayFromZero);
    }
}
