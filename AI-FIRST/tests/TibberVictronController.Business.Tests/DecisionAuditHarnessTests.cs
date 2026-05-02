using System.Text.Json;
using TibberVictronController.Business.Auditing;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class DecisionAuditHarnessTests
{
    [Fact]
    public void GoldenScenarioCreatesReadableAuditForTwentyFourHours()
    {
        var scenario = DecisionAuditGoldenScenarioFactory.Create(initialStateOfChargePercent: 37m);
        var harness = new DecisionAuditHarness(new BatteryForecastSimulator(), new BaselineDecisionEngine());

        var report = harness.Run(scenario);

        Assert.Equal(96, report.DecisionSlots.Count);
        Assert.Equal(96, report.BaselineDecisionSlots.Count);
        Assert.Equal(95m, report.Scenario.BatteryConfiguration.PlanningMaximumStateOfChargePercent);
        Assert.All(report.DecisionSlots, slot =>
        {
            Assert.False(string.IsNullOrWhiteSpace(slot.Action));
            Assert.False(string.IsNullOrWhiteSpace(slot.RuleId));
            Assert.False(string.IsNullOrWhiteSpace(slot.Reason));
            Assert.False(string.IsNullOrWhiteSpace(slot.AlternativeAction));
            Assert.False(string.IsNullOrWhiteSpace(slot.AlternativeRejectedReason));
        });

        Assert.Contains(report.DecisionSlots, slot => slot.Action == "ChargeFromGrid");
        Assert.Contains(report.DecisionSlots, slot => slot.Action == "Discharge");
        Assert.All(report.DecisionSlots, AssertReasonMatchesAction);
        Assert.All(report.DecisionSlots.Where(slot => slot.TibberPricePerKwh < 0m && slot.ExpectedSocPercent < 100m), slot =>
            Assert.Equal("ChargeFromGrid", slot.Action));
        Assert.Contains(report.DecisionSlots, slot =>
            slot.RuleId == BatteryForecastRuleIds.DischargeBeforeNegativePriceWindow &&
            slot.TimeSlot.StartsAtUtc.Hour < 12);
        Assert.Contains(report.DecisionSlots, slot =>
            slot.RuleId == BatteryForecastRuleIds.NegativePriceGridCharge &&
            slot.ExpectedSocPercent - slot.StateOfChargeBeforePercent < 6.25m);
        Assert.Contains(report.DecisionSlots, slot =>
            slot.RuleId == BatteryForecastRuleIds.ExpensivePriceDischarge &&
            slot.StateOfChargeBeforePercent - slot.ExpectedSocPercent > slot.DischargedEnergyKwh / scenario.BatteryConfiguration.TotalCapacityKwh * 100m);
        Assert.Contains(report.DecisionSlots, slot =>
            slot.TimeSlot.StartsAtUtc.Hour >= 21 &&
            slot.RuleId == BatteryForecastRuleIds.EndSocReserve);
        Assert.True(report.Metrics.FinalStateOfChargePercent >= scenario.BatteryConfiguration.TargetEndStateOfChargePercent);
        Assert.All(report.DecisionSlots, slot =>
            Assert.True(slot.ExpectedSocPercent >= scenario.BatteryConfiguration.PlanningMinimumStateOfChargePercent));
        Assert.All(report.DecisionSlots, AssertCandidateRuleIdIsSpecific);
        Assert.Contains("startsAtUtc", DecisionAuditExporter.ExportCsv(report));
        Assert.Contains("\"metrics\"", DecisionAuditExporter.ExportJson(report));
    }

    [Fact]
    public void InvariantValidatorAcceptsGoldenScenario()
    {
        var scenario = DecisionAuditGoldenScenarioFactory.Create(initialStateOfChargePercent: 37m);
        var harness = new DecisionAuditHarness(new BatteryForecastSimulator(), new BaselineDecisionEngine());

        var report = harness.Run(scenario);
        var violations = DecisionAuditInvariantValidator.Validate(report);

        Assert.Empty(violations);
    }

    [Fact]
    public void InvariantValidatorRejectsDischargeAtMinimumStateOfCharge()
    {
        var scenario = DecisionAuditGoldenScenarioFactory.Create(initialStateOfChargePercent: 10m);
        var invalidSlot = new DecisionAuditSlot(
            scenario.PriceForecast[0].TimeSlot,
            Action: "Discharge",
            RuleId: "BrokenRule",
            Reason: "Testverletzung",
            AlternativeAction: "Idle",
            AlternativeRejectedReason: "Test",
            TibberPricePerKwh: 0.40m,
            ExpectedPvYieldKwh: 0m,
            ExpectedConsumptionKwh: 0.25m,
            GridImportKwh: 0m,
            GridExportKwh: 0m,
            ChargedEnergyKwh: 0m,
            DischargedEnergyKwh: 0.25m,
            StateOfChargeBeforePercent: 10m,
            ExpectedSocPercent: 8m,
            TargetPowerWatts: 1000,
            ConstraintFlags: Array.Empty<string>());
        var report = new DecisionAuditReport(
            scenario,
            new[] { invalidSlot },
            new[] { invalidSlot with { Action = "Idle", DischargedEnergyKwh = 0m, TargetPowerWatts = 0 } },
            DecisionAuditMetrics.Empty,
            DecisionAuditMetrics.Empty);

        var violations = DecisionAuditInvariantValidator.Validate(report);

        Assert.Contains(violations, violation => violation.Contains("Kein Discharge, wenn SoC <= MinSocPercent"));
    }

    [Fact]
    public void MetamorphicUniformPositivePriceOffsetKeepsActionSequenceStable()
    {
        var baseScenario = DecisionAuditGoldenScenarioFactory.Create(initialStateOfChargePercent: 45m, includeNegativePrices: false);
        var offsetScenario = baseScenario with
        {
            PriceForecast = baseScenario.PriceForecast
                .Select(slot => new TibberPriceForecastSlot(slot.TimeSlot, slot.TotalPricePerKwh + 0.10m, slot.Currency))
                .ToArray()
        };
        var harness = new DecisionAuditHarness(new BatteryForecastSimulator(), new BaselineDecisionEngine());

        var baseReport = harness.Run(baseScenario);
        var offsetReport = harness.Run(offsetScenario);

        Assert.Equal(
            baseReport.DecisionSlots.Select(slot => slot.Action),
            offsetReport.DecisionSlots.Select(slot => slot.Action));
    }

    [Fact]
    public void MetamorphicHigherInitialStateOfChargeDoesNotIncreaseGridChargedEnergy()
    {
        var lowSocScenario = DecisionAuditGoldenScenarioFactory.Create(initialStateOfChargePercent: 30m);
        var highSocScenario = DecisionAuditGoldenScenarioFactory.Create(initialStateOfChargePercent: 80m);
        var harness = new DecisionAuditHarness(new BatteryForecastSimulator(), new BaselineDecisionEngine());

        var lowSocReport = harness.Run(lowSocScenario);
        var highSocReport = harness.Run(highSocScenario);

        Assert.True(highSocReport.Metrics.GridChargedEnergyKwh <= lowSocReport.Metrics.GridChargedEnergyKwh);
    }

    [Fact]
    public void JsonExportCanBeParsedForExternalReviewTools()
    {
        var scenario = DecisionAuditGoldenScenarioFactory.Create(initialStateOfChargePercent: 37m);
        var harness = new DecisionAuditHarness(new BatteryForecastSimulator(), new BaselineDecisionEngine());
        var report = harness.Run(scenario);

        using var document = JsonDocument.Parse(DecisionAuditExporter.ExportJson(report));

        Assert.True(document.RootElement.TryGetProperty("decisionSlots", out var decisionSlots));
        Assert.Equal(96, decisionSlots.GetArrayLength());
    }

    private static void AssertReasonMatchesAction(DecisionAuditSlot slot)
    {
        if (slot.Action != "Idle")
        {
            return;
        }

        Assert.DoesNotContain("Laden aus dem Netz senkt", slot.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("laedt deshalb aus dem Netz", slot.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertCandidateRuleIdIsSpecific(DecisionAuditSlot slot)
    {
        var acceptedIdleRuleIds = new[]
        {
            BatteryForecastRuleIds.WaitForNegativePriceWindow,
            BatteryForecastRuleIds.EndSocReserve,
            BatteryForecastRuleIds.MinimumSocReserve,
            BatteryForecastRuleIds.NeutralIdle,
            BatteryForecastRuleIds.BatteryFullIdle,
            BatteryForecastRuleIds.BatteryFullPvSurplus,
            BatteryForecastRuleIds.PreserveHeadroomForNegativePrice,
            BatteryForecastRuleIds.PlanningMaximumSocHeadroom
        };
        var acceptedActionRuleIds = new[]
        {
            BatteryForecastRuleIds.NegativePriceGridCharge,
            BatteryForecastRuleIds.PlannedGridCharge,
            BatteryForecastRuleIds.PvSurplusCharge,
            BatteryForecastRuleIds.DischargeBeforeNegativePriceWindow,
            BatteryForecastRuleIds.ExpensivePriceDischarge
        };

        Assert.False(string.IsNullOrWhiteSpace(slot.RuleId));
        Assert.NotEqual("ForecastSimulation", slot.RuleId);

        if (slot.Action == "Idle")
        {
            Assert.Contains(slot.RuleId, acceptedIdleRuleIds);
        }
        else
        {
            Assert.Contains(slot.RuleId, acceptedActionRuleIds);
        }
    }
}
