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
    public void SimulateCoversConsumptionFromBatteryEvenBelowAveragePrice()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(80m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(10m, maximumDischargePowerWatts: 3000, roundTripEfficiencyPercent: 100m);
        var priceForecast = CreatePriceForecast(0.18m, 0.26m, 0.30m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0.50m, 0m, 0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        var entry = result.Entries[0];
        Assert.Equal(BatteryDecisionState.Discharge, entry.Decision.Instruction.DecisionState);
        Assert.Equal(2000, entry.Decision.TargetPowerWatts);
        Assert.Contains(entry.Reasons, reason => reason.RuleName == BatteryForecastRuleIds.SelfConsumptionDischarge);
    }

    [Fact]
    public void SimulateHoldsBatteryEnergyForMoreExpensiveShortage()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(20m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(10m, minimumStateOfChargePercent: 10m, maximumDischargePowerWatts: 3000, roundTripEfficiencyPercent: 100m);
        var priceForecast = CreatePriceForecast(0.20m, 0.50m);
        var pvForecast = CreatePvForecast(0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0.75m, 0.75m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        Assert.Equal(BatteryDecisionState.Discharge, result.Entries[0].Decision.Instruction.DecisionState);
        Assert.Equal(1000, result.Entries[0].Decision.TargetPowerWatts);
        Assert.Contains(result.Entries[0].Reasons, reason => reason.RuleName == BatteryForecastRuleIds.HoldForExpensiveSlot);
        Assert.Equal(BatteryDecisionState.Discharge, result.Entries[1].Decision.Instruction.DecisionState);
        Assert.Equal(3000, result.Entries[1].Decision.TargetPowerWatts);
        Assert.DoesNotContain(result.Entries, entry => entry.Decision.Instruction.DecisionState == BatteryDecisionState.Charge);
    }

    [Fact]
    public void SimulateDoesNotGridChargeWhenShortageIsNotMoreExpensiveThanChargingIncludingLosses()
    {
        // Reproduces the night of 2026-10-14: charging at 0.29 EUR only to avoid 0.30 EUR import loses money through efficiency losses,
        // and the expensive evening slot is covered by PV surplus anyway.
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(30m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(10m, minimumStateOfChargePercent: 15m, maximumChargePowerWatts: 5000, maximumDischargePowerWatts: 5000, roundTripEfficiencyPercent: 90m);
        var priceForecast = CreatePriceForecast(0.29m, 0.29m, 0.30m, 0.30m, 0.25m, 0.45m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m, 0m, 3.00m, 0m);
        var consumptionForecast = CreateConsumptionForecast(1.00m, 1.00m, 1.00m, 1.00m, 1.00m, 1.00m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.09m);

        Assert.DoesNotContain(result.Entries, entry => entry.Decision.Instruction.ChargeSource == BatteryChargeSource.Grid);
        Assert.Equal(BatteryDecisionState.Discharge, result.Entries[0].Decision.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.PV, result.Entries[4].Decision.Instruction.ChargeSource);
        Assert.Equal(BatteryDecisionState.Discharge, result.Entries[5].Decision.Instruction.DecisionState);
    }

    [Fact]
    public void SimulateGridChargesOnlyForClearlyMoreExpensiveShortage()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(30m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(10m, minimumStateOfChargePercent: 15m, maximumChargePowerWatts: 5000, maximumDischargePowerWatts: 5000, roundTripEfficiencyPercent: 90m);
        var priceForecast = CreatePriceForecast(0.29m, 0.29m, 0.30m, 0.30m, 0.25m, 0.45m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m, 0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(1.00m, 1.00m, 1.00m, 1.00m, 0m, 1.00m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.09m);

        var gridChargeEntries = result.Entries
            .Where(entry => entry.Decision.Instruction.ChargeSource == BatteryChargeSource.Grid)
            .ToArray();
        var gridChargeEntry = Assert.Single(gridChargeEntries);
        Assert.Equal(result.Entries[4].TimeSlot, gridChargeEntry.TimeSlot);
        Assert.Contains(gridChargeEntry.Reasons, reason => reason.RuleName == BatteryForecastRuleIds.PlannedGridCharge);
        Assert.Equal(BatteryDecisionState.Discharge, result.Entries[5].Decision.Instruction.DecisionState);
        Assert.Equal(4000, result.Entries[5].Decision.TargetPowerWatts);
    }

    [Fact]
    public void SimulateDischargesBeforeFuturePvSurplus()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(95m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(new BatteryConfigurationValues
        {
            TotalCapacityKwh = 10m,
            MinimumStateOfChargePercent = 10m,
            MaximumDischargePowerWatts = 3000,
            RoundTripEfficiencyPercent = 100m,
            TargetEndStateOfChargePercent = 25m,
            PlanningMinimumStateOfChargePercent = 10m
        });
        var priceForecast = CreatePriceForecast(0.12m, 0.40m, 0.40m);
        var pvForecast = CreatePvForecast(0m, 1.50m, 1.50m);
        var consumptionForecast = CreateConsumptionForecast(0.50m, 0m, 0m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.09m);

        var entry = result.Entries[0];
        Assert.Equal(BatteryDecisionState.Discharge, entry.Decision.Instruction.DecisionState);
        Assert.Contains(entry.Reasons, reason => reason.RuleName == BatteryForecastRuleIds.SelfConsumptionDischarge);
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
    public void SimulateLimitsNeedBasedGridChargingToPlanningMaximum()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(50m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(new BatteryConfigurationValues
        {
            TotalCapacityKwh = 2m,
            MinimumStateOfChargePercent = 10m,
            MaximumChargePowerWatts = 5000,
            MaximumDischargePowerWatts = 3000,
            RoundTripEfficiencyPercent = 100m,
            PlanningMaximumStateOfChargePercent = 90m
        });
        var priceForecast = CreatePriceForecast(0.10m, 0.30m, 0.50m, 0.50m, 0.50m);
        var pvForecast = CreatePvForecast(0m, 0m, 0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0m, 0m, 0.75m, 0.75m, 0.75m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        var entry = result.Entries[0];
        Assert.Equal(BatteryDecisionState.Charge, entry.Decision.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.Grid, entry.Decision.Instruction.ChargeSource);
        Assert.Equal(3200, entry.Decision.TargetPowerWatts);
        Assert.Equal(90m, entry.StateOfChargeAfterPercent);
        Assert.Contains(entry.Reasons, reason => reason.RuleName == BatteryForecastRuleIds.PlannedGridCharge);
        Assert.All(result.Entries, forecastEntry => Assert.True(forecastEntry.StateOfChargeAfterPercent <= 90m));
    }

    [Fact]
    public void SimulateLimitsPvChargingToPlanningMaximum()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(94m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(new BatteryConfigurationValues
        {
            TotalCapacityKwh = 10m,
            MaximumChargePowerWatts = 3000,
            RoundTripEfficiencyPercent = 100m,
            PlanningMaximumStateOfChargePercent = 95m
        });
        var priceForecast = CreatePriceForecast(0.20m);
        var pvForecast = CreatePvForecast(1.00m);
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
        Assert.Equal(400, entry.Decision.TargetPowerWatts);
        Assert.Equal(95m, entry.StateOfChargeAfterPercent);
    }

    [Fact]
    public void SimulatePreventsChargingWithConcreteRuleIdWhenPlanningMaximumIsReached()
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
        var priceForecast = CreatePriceForecast(-0.10m, 0.30m, 0.50m);
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
    public void SimulateAddsGridChargingToCheapPvSlotInsteadOfLaterExpensiveSlot()
    {
        // 2026-10-17: PV alone does not fill the battery; the cheap midday slot must take the grid share,
        // not the 0.35 EUR slot right before the evening shortage.
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(20m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(10m, minimumStateOfChargePercent: 15m, maximumChargePowerWatts: 5000, maximumDischargePowerWatts: 5000, roundTripEfficiencyPercent: 90m);
        var priceForecast = CreatePriceForecast(0.29m, 0.35m, 0.44m, 0.44m);
        var pvForecast = CreatePvForecast(0.75m, 0m, 0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0.25m, 0m, 1.00m, 1.00m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.09m);

        var middaySlot = result.Entries[0];
        Assert.Equal(BatteryDecisionState.Charge, middaySlot.Decision.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.Grid, middaySlot.Decision.Instruction.ChargeSource);
        Assert.Equal(5000, middaySlot.Decision.TargetPowerWatts);
        Assert.Contains("PV-Ueberschuss", middaySlot.Reasons[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SimulateStaysIdleForNegligibleExpectedImport()
    {
        // Reproduces the NullReferenceException of 2026-10-15 22:30: a tiny import without any planned hold.
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(50m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(10m, roundTripEfficiencyPercent: 100m);
        var priceForecast = CreatePriceForecast(0.30m, 0.30m);
        var pvForecast = CreatePvForecast(0m, 0m);
        var consumptionForecast = CreateConsumptionForecast(0.0005m, 0.0005m);

        var result = simulator.Simulate(
            priceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh: 0.08m);

        Assert.All(result.Entries, entry => Assert.Equal(BatteryDecisionState.Idle, entry.Decision.Instruction.DecisionState));
        Assert.All(result.Entries, entry => Assert.Equal(BatteryForecastRuleIds.NeutralIdle, entry.Reasons[0].RuleName));
    }

    [Fact]
    public void SimulateDoesNotGridChargeWithoutConsumptionNeed()
    {
        var simulator = new BatteryForecastSimulator();
        var batteryState = new BatteryState(40m, ForecastStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000, roundTripEfficiencyPercent: 100m);
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

        Assert.All(result.Entries, entry => Assert.Equal(BatteryDecisionState.Idle, entry.Decision.Instruction.DecisionState));
        Assert.All(result.Entries, entry => Assert.Equal(40m, entry.StateOfChargeAfterPercent));
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
