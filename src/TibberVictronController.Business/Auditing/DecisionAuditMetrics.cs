namespace TibberVictronController.Business.Auditing;

/// <summary>
/// Summarizes technical and economic effects of an audited decision sequence.
/// </summary>
public sealed record DecisionAuditMetrics(
    decimal TotalCost,
    decimal GridImportKwh,
    decimal GridExportKwh,
    decimal PvUtilizationPercent,
    decimal ChargedEnergyKwh,
    decimal GridChargedEnergyKwh,
    decimal PvChargedEnergyKwh,
    decimal DischargedEnergyKwh,
    decimal CycleCount,
    decimal MinimumStateOfChargePercent,
    decimal MaximumStateOfChargePercent,
    decimal FinalStateOfChargePercent,
    decimal EfficiencyLossKwh,
    decimal EfficiencyLossCost,
    decimal NetBenefitAfterEfficiencyLoss)
{
    public static DecisionAuditMetrics Empty { get; } = new(
        TotalCost: 0m,
        GridImportKwh: 0m,
        GridExportKwh: 0m,
        PvUtilizationPercent: 0m,
        ChargedEnergyKwh: 0m,
        GridChargedEnergyKwh: 0m,
        PvChargedEnergyKwh: 0m,
        DischargedEnergyKwh: 0m,
        CycleCount: 0m,
        MinimumStateOfChargePercent: 0m,
        MaximumStateOfChargePercent: 0m,
        FinalStateOfChargePercent: 0m,
        EfficiencyLossKwh: 0m,
        EfficiencyLossCost: 0m,
        NetBenefitAfterEfficiencyLoss: 0m);
}
