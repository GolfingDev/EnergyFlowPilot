using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Auditing;

/// <summary>
/// Converts a forecast decision into physical grid and battery energy movement.
/// </summary>
public static class DecisionAuditEnergyCalculator
{
    /// <summary>
    /// Calculates final grid import, export and battery movement for one forecast entry.
    /// </summary>
    public static DecisionAuditEnergyMovement Calculate(BatteryForecastEntry entry)
    {
        var decisionEnergyKwh = entry.Decision.TargetPowerWatts / 1000m *
            (decimal)entry.TimeSlot.Duration.TotalHours;
        var baseGridImportKwh = entry.ExpectedGridImportBeforeBatteryKwh;

        return entry.Decision.Instruction.DecisionState switch
        {
            BatteryDecisionState.Charge when entry.Decision.Instruction.ChargeSource == BatteryChargeSource.Grid =>
                new DecisionAuditEnergyMovement(
                    GridImportKwh: Math.Max(0m, baseGridImportKwh) + decisionEnergyKwh,
                    GridExportKwh: Math.Max(0m, -baseGridImportKwh),
                    ChargedEnergyKwh: decisionEnergyKwh,
                    GridChargedEnergyKwh: decisionEnergyKwh,
                    PvChargedEnergyKwh: 0m,
                    DischargedEnergyKwh: 0m),
            BatteryDecisionState.Charge when entry.Decision.Instruction.ChargeSource == BatteryChargeSource.PV =>
                new DecisionAuditEnergyMovement(
                    GridImportKwh: Math.Max(0m, baseGridImportKwh),
                    GridExportKwh: Math.Max(0m, -baseGridImportKwh - decisionEnergyKwh),
                    ChargedEnergyKwh: decisionEnergyKwh,
                    GridChargedEnergyKwh: 0m,
                    PvChargedEnergyKwh: decisionEnergyKwh,
                    DischargedEnergyKwh: 0m),
            BatteryDecisionState.Discharge =>
                new DecisionAuditEnergyMovement(
                    GridImportKwh: Math.Max(0m, baseGridImportKwh - decisionEnergyKwh),
                    GridExportKwh: Math.Max(0m, -baseGridImportKwh),
                    ChargedEnergyKwh: 0m,
                    GridChargedEnergyKwh: 0m,
                    PvChargedEnergyKwh: 0m,
                    DischargedEnergyKwh: decisionEnergyKwh),
            _ =>
                new DecisionAuditEnergyMovement(
                    GridImportKwh: Math.Max(0m, baseGridImportKwh),
                    GridExportKwh: Math.Max(0m, -baseGridImportKwh),
                    ChargedEnergyKwh: 0m,
                    GridChargedEnergyKwh: 0m,
                    PvChargedEnergyKwh: 0m,
                    DischargedEnergyKwh: 0m)
        };
    }
}

public sealed record DecisionAuditEnergyMovement(
    decimal GridImportKwh,
    decimal GridExportKwh,
    decimal ChargedEnergyKwh,
    decimal GridChargedEnergyKwh,
    decimal PvChargedEnergyKwh,
    decimal DischargedEnergyKwh);
