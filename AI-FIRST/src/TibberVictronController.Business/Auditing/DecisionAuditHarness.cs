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

                return new DecisionAuditSlot(
                    entry.TimeSlot,
                    MapAction(entry.Decision.Instruction),
                    entry.Reasons[0].RuleName,
                    entry.Reasons[0].Message,
                    MapAction(alternativeEntry.Decision.Instruction),
                    alternativeRejectedReason,
                    entry.TibberPricePerKwh,
                    entry.ExpectedPvYieldKwh,
                    entry.ExpectedConsumptionKwh,
                    energyMovement.GridImportKwh,
                    energyMovement.GridExportKwh,
                    energyMovement.ChargedEnergyKwh,
                    energyMovement.DischargedEnergyKwh,
                    entry.StateOfChargeBeforePercent,
                    entry.StateOfChargeAfterPercent,
                    entry.Decision.TargetPowerWatts);
            })
            .ToArray();
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
}
