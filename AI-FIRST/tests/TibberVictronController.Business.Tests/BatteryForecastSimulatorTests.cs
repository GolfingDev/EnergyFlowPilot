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
        var batteryConfiguration = new BatteryConfiguration(10m, maximumChargePowerWatts: 2000);
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
        var batteryConfiguration = new BatteryConfiguration(10m, maximumChargePowerWatts: 3000);
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
        var batteryConfiguration = new BatteryConfiguration(10m, maximumChargePowerWatts: 3000);
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
        var batteryConfiguration = new BatteryConfiguration(10m, maximumDischargePowerWatts: 3000);
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
        var batteryConfiguration = new BatteryConfiguration(10m, minimumStateOfChargePercent: 10m, maximumDischargePowerWatts: 3000);
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
