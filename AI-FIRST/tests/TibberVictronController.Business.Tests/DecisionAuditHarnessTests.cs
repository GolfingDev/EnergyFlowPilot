using System.Text.Json;
using TibberVictronController.Business.Auditing;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class DecisionAuditHarnessTests
{
    private static readonly DateTimeOffset ScenarioStartsAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GoldenScenarioCreatesReadableAuditForTwentyFourHours()
    {
        var scenario = CreateScenario(initialStateOfChargePercent: 37m);
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
        var scenario = CreateScenario(initialStateOfChargePercent: 37m);
        var harness = new DecisionAuditHarness(new BatteryForecastSimulator(), new BaselineDecisionEngine());

        var report = harness.Run(scenario);
        var violations = DecisionAuditInvariantValidator.Validate(report);

        Assert.Empty(violations);
    }

    [Fact]
    public void InvariantValidatorRejectsDischargeAtMinimumStateOfCharge()
    {
        var scenario = CreateScenario(initialStateOfChargePercent: 10m);
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
        var baseScenario = CreateScenario(initialStateOfChargePercent: 45m, includeNegativePrices: false);
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
        var lowSocScenario = CreateScenario(initialStateOfChargePercent: 30m);
        var highSocScenario = CreateScenario(initialStateOfChargePercent: 80m);
        var harness = new DecisionAuditHarness(new BatteryForecastSimulator(), new BaselineDecisionEngine());

        var lowSocReport = harness.Run(lowSocScenario);
        var highSocReport = harness.Run(highSocScenario);

        Assert.True(highSocReport.Metrics.GridChargedEnergyKwh <= lowSocReport.Metrics.GridChargedEnergyKwh);
    }

    [Fact]
    public void JsonExportCanBeParsedForExternalReviewTools()
    {
        var scenario = CreateScenario(initialStateOfChargePercent: 37m);
        var harness = new DecisionAuditHarness(new BatteryForecastSimulator(), new BaselineDecisionEngine());
        var report = harness.Run(scenario);

        using var document = JsonDocument.Parse(DecisionAuditExporter.ExportJson(report));

        Assert.True(document.RootElement.TryGetProperty("decisionSlots", out var decisionSlots));
        Assert.Equal(96, decisionSlots.GetArrayLength());
    }

    private static DecisionAuditScenario CreateScenario(
        decimal initialStateOfChargePercent,
        bool includeNegativePrices = true)
    {
        var timeSlots = CreateTimeSlots(ScenarioStartsAtUtc, slotCount: 96);
        var prices = timeSlots
            .Select(timeSlot => new TibberPriceForecastSlot(timeSlot, DeterminePrice(timeSlot.StartsAtUtc, includeNegativePrices), "EUR"))
            .ToArray();
        var pvYield = timeSlots
            .Select(timeSlot => new PvYieldForecastSlot(timeSlot, DeterminePvYieldKwh(timeSlot.StartsAtUtc)))
            .ToArray();
        var consumption = timeSlots
            .Select(timeSlot => new ConsumptionForecastSlot(timeSlot, DetermineConsumptionKwh(timeSlot.StartsAtUtc)))
            .ToArray();

        return new DecisionAuditScenario(
            Name: "Golden 24h Mehrfamilienhaus",
            InitialBatteryState: new BatteryState(initialStateOfChargePercent, ScenarioStartsAtUtc),
            BatteryConfiguration: new BatteryConfiguration(
                totalCapacityKwh: 12m,
                minimumStateOfChargePercent: 10m,
                maximumChargePowerWatts: 3000,
                maximumDischargePowerWatts: 3000,
                roundTripEfficiencyPercent: 90m),
            PriceForecast: prices,
            PvForecast: pvYield,
            ConsumptionForecast: consumption,
            FeedInCompensationPricePerKwh: 0.08m);
    }

    private static IReadOnlyList<ForecastTimeSlot> CreateTimeSlots(DateTimeOffset startsAtUtc, int slotCount)
    {
        return Enumerable.Range(0, slotCount)
            .Select(index => new ForecastTimeSlot(
                startsAtUtc.AddMinutes(index * 15),
                startsAtUtc.AddMinutes((index + 1) * 15)))
            .ToArray();
    }

    private static decimal DeterminePrice(DateTimeOffset slotStartUtc, bool includeNegativePrices)
    {
        if (includeNegativePrices && slotStartUtc.Hour is >= 12 and < 15)
        {
            return -0.05m;
        }

        if (slotStartUtc.Hour is >= 18 and < 21)
        {
            return 0.48m;
        }

        if (slotStartUtc.Hour is >= 2 and < 5)
        {
            return 0.18m;
        }

        return 0.30m;
    }

    private static decimal DeterminePvYieldKwh(DateTimeOffset slotStartUtc)
    {
        return slotStartUtc.Hour switch
        {
            >= 12 and < 15 => 0.20m,
            >= 10 and < 17 => 0.35m,
            >= 8 and < 17 => 0.25m,
            _ => 0m
        };
    }

    private static decimal DetermineConsumptionKwh(DateTimeOffset slotStartUtc)
    {
        return slotStartUtc.Hour switch
        {
            >= 6 and < 9 => 0.45m,
            >= 17 and < 21 => 0.55m,
            >= 0 and < 5 => 0.12m,
            _ => 0.25m
        };
    }
}
