using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Auditing;

/// <summary>
/// Runs candidate and baseline Decision Engine simulations and converts them into readable audit output.
/// </summary>
public sealed class DecisionAuditHarness
{
    private readonly BatteryForecastSimulator batteryForecastSimulator;
    private readonly BaselineDecisionEngine baselineDecisionEngine;

    public DecisionAuditHarness(
        BatteryForecastSimulator batteryForecastSimulator,
        BaselineDecisionEngine baselineDecisionEngine)
    {
        this.batteryForecastSimulator = batteryForecastSimulator;
        this.baselineDecisionEngine = baselineDecisionEngine;
    }

    /// <summary>
    /// Executes one audit scenario and calculates candidate, baseline and benefit metrics.
    /// </summary>
    public DecisionAuditReport Run(DecisionAuditScenario scenario)
    {
        ValidateScenario(scenario);

        var candidateForecast = batteryForecastSimulator.Simulate(
            scenario.PriceForecast,
            scenario.PvForecast,
            scenario.ConsumptionForecast,
            scenario.InitialBatteryState,
            scenario.BatteryConfiguration,
            scenario.FeedInCompensationPricePerKwh);
        var baselineForecast = baselineDecisionEngine.Simulate(scenario);
        var baselineSlotsWithoutAlternative = ConvertEntriesToAuditSlots(
            baselineForecast,
            baselineForecast,
            scenario,
            alternativeRejectedReason: "Baseline ist die Vergleichsstrategie.");
        var candidateSlots = ConvertEntriesToAuditSlots(
            candidateForecast.Entries,
            baselineForecast,
            scenario,
            alternativeRejectedReason: "Die Baseline-Aktion wurde durch die Candidate-Entscheidung ersetzt.");
        var baselineMetrics = DecisionAuditMetricsCalculator.Calculate(
            baselineSlotsWithoutAlternative,
            scenario,
            baselineCost: null);
        var candidateMetrics = DecisionAuditMetricsCalculator.Calculate(
            candidateSlots,
            scenario,
            baselineMetrics.TotalCost);

        return new DecisionAuditReport(
            scenario,
            candidateSlots,
            baselineSlotsWithoutAlternative,
            candidateMetrics,
            baselineMetrics);
    }

    private static IReadOnlyList<DecisionAuditSlot> ConvertEntriesToAuditSlots(
        IReadOnlyList<BatteryForecastEntry> entries,
        IReadOnlyList<BatteryForecastEntry> alternativeEntries,
        DecisionAuditScenario scenario,
        string alternativeRejectedReason)
    {
        var alternativeEntriesByTimeSlot = alternativeEntries.ToDictionary(entry => entry.TimeSlot);

        return entries
            .Select(entry =>
            {
                var alternativeEntry = alternativeEntriesByTimeSlot[entry.TimeSlot];
                var energyMovement = DecisionAuditEnergyCalculator.Calculate(entry);
                var ruleId = entry.Reasons[0].RuleName;
                var alternative = CreateAlternativeExplanation(ruleId, alternativeEntry.Decision.Instruction);

                return new DecisionAuditSlot(
                    entry.TimeSlot,
                    MapAction(entry.Decision.Instruction),
                    ruleId,
                    entry.Reasons[0].Message,
                    alternative.Action,
                    alternative.RejectedReason ?? alternativeRejectedReason,
                    entry.TibberPricePerKwh,
                    entry.ExpectedPvYieldKwh,
                    entry.ExpectedConsumptionKwh,
                    energyMovement.GridImportKwh,
                    energyMovement.GridExportKwh,
                    energyMovement.ChargedEnergyKwh,
                    energyMovement.DischargedEnergyKwh,
                    entry.StateOfChargeBeforePercent,
                    entry.StateOfChargeAfterPercent,
                    entry.Decision.TargetPowerWatts,
                    CreateConstraintFlags(ruleId));
            })
            .ToArray();
    }

    private static DecisionAlternative CreateAlternativeExplanation(
        string ruleId,
        BatteryDecisionInstruction baselineInstruction)
    {
        return ruleId switch
        {
            BatteryForecastRuleIds.WaitForNegativePriceWindow => new DecisionAlternative(
                "ChargeFromGrid",
                "Spaeteres Laden ist guenstiger."),
            BatteryForecastRuleIds.EndSocReserve => new DecisionAlternative(
                "Discharge",
                "Konfigurierte Endreserve wuerde unterschritten."),
            BatteryForecastRuleIds.MinimumSocReserve => new DecisionAlternative(
                "Discharge",
                "Minimaler Akkuladestand wuerde unterschritten."),
            BatteryForecastRuleIds.PreserveHeadroomForNegativePrice => new DecisionAlternative(
                "ChargeFromPV",
                "Spaeteres Laden zu negativen Preisen ist wertvoller als die konfigurierte Einspeiseverguetung."),
            BatteryForecastRuleIds.PlanningMaximumSocHeadroom => new DecisionAlternative(
                "ChargeFromGrid",
                "Das Planungs-Maximum haelt Kapazitaet fuer moegliche PV-Prognoseabweichungen frei."),
            _ => new DecisionAlternative(
                MapAction(baselineInstruction),
                null)
        };
    }

    private static IReadOnlyList<string> CreateConstraintFlags(string ruleId)
    {
        return ruleId switch
        {
            BatteryForecastRuleIds.WaitForNegativePriceWindow => new[] { "WAITING_FOR_NEGATIVE_PRICE" },
            BatteryForecastRuleIds.EndSocReserve => new[] { "END_SOC_RESERVE_PROTECTED" },
            BatteryForecastRuleIds.MinimumSocReserve => new[] { "MIN_SOC_PROTECTED" },
            BatteryForecastRuleIds.PlanningMaximumSocHeadroom => new[] { "PLANNING_MAX_SOC_PROTECTED" },
            BatteryForecastRuleIds.BatteryFullIdle or BatteryForecastRuleIds.BatteryFullPvSurplus => new[] { "BATTERY_FULL" },
            BatteryForecastRuleIds.NegativePriceGridCharge or BatteryForecastRuleIds.PlannedGridCharge or BatteryForecastRuleIds.PvSurplusCharge => new[] { "EFFICIENCY_APPLIED" },
            BatteryForecastRuleIds.DischargeBeforeNegativePriceWindow => new[] { "NEGATIVE_PRICE_ANTICIPATED", "EFFICIENCY_APPLIED" },
            BatteryForecastRuleIds.ExpensivePriceDischarge => new[] { "END_SOC_RESERVE_PROTECTED", "EFFICIENCY_APPLIED" },
            _ => Array.Empty<string>()
        };
    }

    private static string MapAction(BatteryDecisionInstruction instruction)
    {
        return instruction.DecisionState switch
        {
            BatteryDecisionState.Charge when instruction.ChargeSource == BatteryChargeSource.Grid => "ChargeFromGrid",
            BatteryDecisionState.Charge when instruction.ChargeSource == BatteryChargeSource.PV => "ChargeFromPV",
            BatteryDecisionState.Discharge => "Discharge",
            _ => "Idle"
        };
    }

    private static void ValidateScenario(DecisionAuditScenario scenario)
    {
        if (scenario is null)
        {
            throw new ArgumentNullException(nameof(scenario), "Das Audit-Szenario darf nicht null sein.");
        }

        if (string.IsNullOrWhiteSpace(scenario.Name))
        {
            throw new ArgumentException("Das Audit-Szenario braucht einen Namen.", nameof(scenario));
        }

        if (scenario.PriceForecast.Count == 0)
        {
            throw new ArgumentException("Das Audit-Szenario braucht Tibber-Preise.", nameof(scenario));
        }

        if (scenario.FeedInCompensationPricePerKwh < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(scenario), "Die Einspeiseverguetung darf nicht negativ sein.");
        }
    }

    private sealed record DecisionAlternative(string Action, string? RejectedReason);
}
