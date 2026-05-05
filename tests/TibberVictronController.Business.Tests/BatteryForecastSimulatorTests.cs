using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class BatteryForecastSimulatorTests
{
    private static readonly DateTimeOffset ForecastStartsAtUtc = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SimulateChoosesCheapestSlotsAndUpdatesStateOfChargeSlotBySlot()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(90m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(10m, maximumChargePowerWatts: 2000, roundTripEfficiencyPercent: 100m);
        var priceForecast = CreatePriceForecast(0.20m, -0.30m, -0.10m, -0.40m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0m, 0m, 0m, 0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        Assert.Equal(BatteryDecisionState.Idle, result.Entries[0].Decision.Instruction.DecisionState);
        Assert.Equal(BatteryDecisionState.Charge, result.Entries[1].Decision.Instruction.DecisionState);
        Assert.Equal(BatteryDecisionState.Idle, result.Entries[2].Decision.Instruction.DecisionState);
        Assert.Equal(BatteryDecisionState.Charge, result.Entries[3].Decision.Instruction.DecisionState);
        Assert.Equal(90m, result.Entries[0].StateOfChargeBeforePercent);
        Assert.Equal(95m, result.Entries[1].StateOfChargeAfterPercent);
        Assert.Equal(100m, result.Entries[3].StateOfChargeAfterPercent);
    }

    [Fact]
    public void SimulateChargesFromPvSurplusBeforeTibberPriceStrategy()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(50m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(10m, maximumChargePowerWatts: 3000, roundTripEfficiencyPercent: 100m);
        var priceForecast = CreatePriceForecast(0.50m);
        var pvForecast = CreatePvForecast(1.00m);
        var consumptionForecast = CreateConsumptionForecast(0.25m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        var entry = result.Entries.Single();
        Assert.Equal(BatteryDecisionState.Charge, entry.Decision.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.PV, entry.Decision.Instruction.ChargeSource);
        Assert.Equal(3000, entry.Decision.TargetPowerWatts);
        Assert.Equal(57.5m, entry.StateOfChargeAfterPercent);
    }

    [Fact]
    public void SimulateKeepsPvHeadroomWhenFutureNegativePriceIsMoreValuableThanFeedInCompensation()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(80m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(10m, maximumChargePowerWatts: 3000, roundTripEfficiencyPercent: 100m);
        var priceForecast = CreatePriceForecast(0.10m, -0.30m);
        var pvForecast = CreatePvForecast(1.00m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0.25m, 0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        Assert.Equal(BatteryDecisionState.Idle, result.Entries[0].Decision.Instruction.DecisionState);
        Assert.Contains(result.Entries[0].Reasons, reason => reason.Message.Contains("Kapazitaet fuer spaetere negative Tibber-Preise"));
    }

    [Fact]
    public void SimulateDischargesOnlyUpToExpectedGridImportDuringExpensivePricePhase()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(80m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(10m, maximumDischargePowerWatts: 3000, roundTripEfficiencyPercent: 100m);
        var priceForecast = CreatePriceForecast(0.60m, 0.10m, 0.20m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0.50m, 0m, 0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        var expensiveEntry = result.Entries[0];
        Assert.Equal(BatteryDecisionState.Discharge, expensiveEntry.Decision.Instruction.DecisionState);
        Assert.Equal(2000, expensiveEntry.Decision.TargetPowerWatts);
        Assert.Equal(75m, expensiveEntry.StateOfChargeAfterPercent);
    }

    [Fact]
    public void SimulateDoesNotDischargeBelowConfiguredMinimumStateOfCharge()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(12m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(10m, minimumStateOfChargePercent: 10m, maximumDischargePowerWatts: 3000, roundTripEfficiencyPercent: 100m);
        var priceForecast = CreatePriceForecast(0.60m, 0.10m, 0.20m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0.50m, 0m, 0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        var expensiveEntry = result.Entries[0];
        Assert.Equal(BatteryDecisionState.Discharge, expensiveEntry.Decision.Instruction.DecisionState);
        Assert.Equal(800, expensiveEntry.Decision.TargetPowerWatts);
        Assert.Equal(10m, expensiveEntry.StateOfChargeAfterPercent);
    }

    [Fact]
    public void SimulateLeavesFullBatteryIdleWhenChargingWouldBeRequired()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(100m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(10m, maximumChargePowerWatts: 3000);
        var priceForecast = CreatePriceForecast(-0.30m);
        var pvForecast = CreatePvForecast(0m);
        var consumptionForecast = CreateConsumptionForecast(0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        var entry = result.Entries.Single();
        Assert.Equal(BatteryDecisionState.Idle, entry.Decision.Instruction.DecisionState);
        Assert.Equal(0, entry.Decision.TargetPowerWatts);
        Assert.Equal(100m, entry.StateOfChargeAfterPercent);
    }

    [Fact]
    public void SimulateAppliesRoundTripEfficiencyWhenCharging()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(50m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000, roundTripEfficiencyPercent: 90m);
        var priceForecast = CreatePriceForecast(-0.05m);
        var pvForecast = CreatePvForecast(0m);
        var consumptionForecast = CreateConsumptionForecast(0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        var entry = result.Entries.Single();
        Assert.Equal(BatteryDecisionState.Charge, entry.Decision.Instruction.DecisionState);
        Assert.True(entry.StateOfChargeAfterPercent < 56.25m);
        Assert.Equal(55.9293m, entry.StateOfChargeAfterPercent);
    }

    [Fact]
    public void SimulateAppliesRoundTripEfficiencyWhenDischarging()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(50m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, minimumStateOfChargePercent: 10m, maximumDischargePowerWatts: 3000, roundTripEfficiencyPercent: 90m);
        var priceForecast = CreatePriceForecast(0.50m, 0.10m, 0.20m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0.75m, 0m, 0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        var entry = result.Entries[0];
        Assert.Equal(BatteryDecisionState.Discharge, entry.Decision.Instruction.DecisionState);
        Assert.True(entry.StateOfChargeAfterPercent < 43.75m);
        Assert.Equal(43.4119m, entry.StateOfChargeAfterPercent);
    }

    [Fact]
    public void SimulateKeepsOneToOneStateOfChargeWhenEfficiencyIsOneHundredPercent()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(50m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000, roundTripEfficiencyPercent: 100m);
        var priceForecast = CreatePriceForecast(-0.05m);
        var pvForecast = CreatePvForecast(0m);
        var consumptionForecast = CreateConsumptionForecast(0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        Assert.Equal(56.25m, result.Entries.Single().StateOfChargeAfterPercent);
    }

    [Fact]
    public void SimulateDischargesBeforeFutureNegativePriceWindow()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(37m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, minimumStateOfChargePercent: 10m, maximumChargePowerWatts: 3000, maximumDischargePowerWatts: 3000);
        var priceForecast = CreatePriceForecast(0.30m, 0.30m, -0.05m, -0.05m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0.50m, 0.50m, 0m, 0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        Assert.Equal(BatteryDecisionState.Discharge, result.Entries[0].Decision.Instruction.DecisionState);
        Assert.Contains(result.Entries[0].Reasons, reason => reason.RuleName == "DISCHARGE_BEFORE_NEGATIVE_PRICE_WINDOW");
        Assert.Equal(BatteryDecisionState.Charge, result.Entries[2].Decision.Instruction.DecisionState);
        Assert.True(result.Entries[2].StateOfChargeAfterPercent > result.Entries[1].StateOfChargeAfterPercent);
        Assert.All(result.Entries, entry => Assert.True(entry.StateOfChargeAfterPercent >= batteryConfiguration.MinimumStateOfChargePercent));
    }

    [Theory]
    [InlineData(25, false)]
    [InlineData(10, true)]
    public void SimulateAppliesConfiguredEndStateOfChargeReserveAfterPeak(decimal targetEndSocPercent, bool allowsFurtherDischarge)
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(26m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(
            12m,
            minimumStateOfChargePercent: 10m,
            maximumDischargePowerWatts: 3000,
            roundTripEfficiencyPercent: 100m,
            targetEndStateOfChargePercent: targetEndSocPercent);
        var priceForecast = CreatePriceForecast(0.30m, 0.30m, 0.30m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0.50m, 0.50m, 0.50m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        if (allowsFurtherDischarge)
        {
            Assert.Contains(result.Entries, entry => entry.Decision.Instruction.DecisionState == BatteryDecisionState.Discharge);
        }
        else
        {
            Assert.All(result.Entries, entry => Assert.True(entry.StateOfChargeAfterPercent >= targetEndSocPercent));
            Assert.Contains(result.Entries, entry => entry.Reasons.Any(reason => reason.RuleName == "END_SOC_RESERVE"));
        }
    }

    [Fact]
    public void SimulateLimitsGridChargingToPlanningMaximumWhenPriceIsAboveFeedInCompensation()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(88m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(new BatteryConfigurationValues
        {
            TotalCapacityKwh = 12m,
            MaximumChargePowerWatts = 3000,
            RoundTripEfficiencyPercent = 100m,
            PlanningMaximumStateOfChargePercent = 90m
        });
        var priceForecast = CreatePriceForecast(0.10m, 0.30m, 0.50m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0m, 0m, 0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        var entry = result.Entries[0];
        Assert.Equal(BatteryDecisionState.Charge, entry.Decision.Instruction.DecisionState);
        Assert.Equal(960, entry.Decision.TargetPowerWatts);
        Assert.Equal(90m, entry.StateOfChargeAfterPercent);
        Assert.Contains(entry.Reasons, reason => reason.RuleName == BatteryForecastRuleIds.PlanningMaximumGridChargeLimit);
        Assert.Contains(entry.Reasons, reason => reason.Message.Contains("Planungs-Maximum"));
    }

    [Fact]
    public void SimulatePreventsGridChargingWithConcreteRuleIdWhenPlanningMaximumIsReached()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(90m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(new BatteryConfigurationValues
        {
            TotalCapacityKwh = 12m,
            MaximumChargePowerWatts = 3000,
            RoundTripEfficiencyPercent = 100m,
            PlanningMaximumStateOfChargePercent = 90m
        });
        var priceForecast = CreatePriceForecast(0.10m, 0.30m, 0.50m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0m, 0m, 0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        var entry = result.Entries[0];
        Assert.Equal(BatteryDecisionState.Idle, entry.Decision.Instruction.DecisionState);
        Assert.Equal(0, entry.Decision.TargetPowerWatts);
        Assert.Equal(90m, entry.StateOfChargeAfterPercent);
        Assert.Contains(entry.Reasons, reason => reason.RuleName == BatteryForecastRuleIds.PlanningMaximumSocHeadroom);
    }

    [Fact]
    public void SimulateAllowsGridChargingAbovePlanningMaximumWhenPriceIsBelowFeedInCompensation()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(88m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(new BatteryConfigurationValues
        {
            TotalCapacityKwh = 12m,
            MaximumChargePowerWatts = 3000,
            RoundTripEfficiencyPercent = 100m,
            PlanningMaximumStateOfChargePercent = 90m
        });
        var priceForecast = CreatePriceForecast(0.05m, 0.30m, 0.50m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0m, 0m, 0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        var entry = result.Entries[0];
        Assert.Equal(BatteryDecisionState.Charge, entry.Decision.Instruction.DecisionState);
        Assert.Equal(3000, entry.Decision.TargetPowerWatts);
        Assert.True(entry.StateOfChargeAfterPercent > batteryConfiguration.PlanningMaximumStateOfChargePercent);
    }

    private static IReadOnlyList<TibberPriceForecastSlot> CreatePriceForecast(params decimal[] prices)
    {
        return prices
            .Select((price, index) => new TibberPriceForecastSlot(CreateTimeSlot(index), price, "EUR"))
            .ToArray();
    }

    private static IReadOnlyList<PvYieldForecastSlot> CreatePvForecast(params decimal[] expectedPvYieldKwh)
    {
        return expectedPvYieldKwh
            .Select((pvYield, index) => new PvYieldForecastSlot(CreateTimeSlot(index), pvYield))
            .ToArray();
    }

    private static IReadOnlyList<ConsumptionForecastSlot> CreateConsumptionForecast(params decimal[] expectedConsumptionKwh)
    {
        return expectedConsumptionKwh
            .Select((consumption, index) => new ConsumptionForecastSlot(CreateTimeSlot(index), consumption))
            .ToArray();
    }

    private static ForecastTimeSlot CreateTimeSlot(int index)
    {
        var startsAtUtc = ForecastStartsAtUtc.AddMinutes(15 * index);

        return new ForecastTimeSlot(startsAtUtc, startsAtUtc.AddMinutes(15));
    }
}
