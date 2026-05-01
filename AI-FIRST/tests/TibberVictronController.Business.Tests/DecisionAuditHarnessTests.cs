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
            TargetPowerWatts: 1000);
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
}
