using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Tests.TestDoubles;

namespace TibberVictronController.Business.Tests;

public sealed class BatteryDecisionRuleEvaluatorTests
{
    private static readonly DateTimeOffset TestDayStartsAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EvaluateChargesFromPvWhenPvYieldExceedsConsumptionEvenDuringExpensiveTibberPhase()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(18);
        var priceForecast = await CreatePriceForecastAsync();
        var forecastSlot = new ForecastTimeSlot(decisionTimeUtc, decisionTimeUtc.AddMinutes(15));
        var pvYieldSlot = new PvYieldForecastSlot(forecastSlot, 1.20m);
        var consumptionSlot = new ConsumptionForecastSlot(forecastSlot, 0.30m);
        var batteryState = CreateBatteryState(55m);
        var evaluator = new BatteryDecisionRuleEvaluator();

        var result = evaluator.Evaluate(decisionTimeUtc, priceForecast, pvYieldSlot, consumptionSlot, batteryState);

        Assert.Equal(BatteryDecisionState.Charge, result.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.PV, result.Instruction.ChargeSource);
        Assert.Contains(result.Reasons, reason => reason.Message.Contains("PV-Ertrag"));
    }

    [Fact]
    public async Task EvaluateDoesNotChargeFromPvWhenBatteryIsFull()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(18);
        var priceForecast = await CreatePriceForecastAsync();
        var forecastSlot = new ForecastTimeSlot(decisionTimeUtc, decisionTimeUtc.AddMinutes(15));
        var pvYieldSlot = new PvYieldForecastSlot(forecastSlot, 1.20m);
        var consumptionSlot = new ConsumptionForecastSlot(forecastSlot, 0.30m);
        var fullBatteryState = CreateBatteryState(100m);
        var evaluator = new BatteryDecisionRuleEvaluator();

        var result = evaluator.Evaluate(decisionTimeUtc, priceForecast, pvYieldSlot, consumptionSlot, fullBatteryState);

        Assert.Equal(BatteryDecisionState.Idle, result.Instruction.DecisionState);
        Assert.Contains(result.Reasons, reason => reason.Message.Contains("Akku ist voll"));
    }

    [Fact]
    public async Task EvaluateFallsBackToTibberPriceStrategyWhenPvYieldDoesNotExceedConsumption()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(18);
        var priceForecast = await CreatePriceForecastAsync();
        var forecastSlot = new ForecastTimeSlot(decisionTimeUtc, decisionTimeUtc.AddMinutes(15));
        var pvYieldSlot = new PvYieldForecastSlot(forecastSlot, 0.10m);
        var consumptionSlot = new ConsumptionForecastSlot(forecastSlot, 0.30m);
        var batteryState = CreateBatteryState(55m);
        var evaluator = new BatteryDecisionRuleEvaluator();

        var result = evaluator.Evaluate(decisionTimeUtc, priceForecast, pvYieldSlot, consumptionSlot, batteryState);

        Assert.Equal(BatteryDecisionState.Discharge, result.Instruction.DecisionState);
        Assert.Null(result.Instruction.ChargeSource);
    }

    [Fact]
    public async Task EvaluateRejectsDifferentPvAndConsumptionTimeSlots()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(12);
        var priceForecast = await CreatePriceForecastAsync();
        var pvTimeSlot = new ForecastTimeSlot(decisionTimeUtc, decisionTimeUtc.AddMinutes(15));
        var consumptionTimeSlot = new ForecastTimeSlot(decisionTimeUtc.AddMinutes(15), decisionTimeUtc.AddMinutes(30));
        var pvYieldSlot = new PvYieldForecastSlot(pvTimeSlot, 0.40m);
        var consumptionSlot = new ConsumptionForecastSlot(consumptionTimeSlot, 0.30m);
        var batteryState = CreateBatteryState(55m);
        var evaluator = new BatteryDecisionRuleEvaluator();

        var exception = Assert.Throws<ArgumentException>(
            () => evaluator.Evaluate(decisionTimeUtc, priceForecast, pvYieldSlot, consumptionSlot, batteryState));

        Assert.Contains("PV-Ertrag und Verbrauch muessen denselben Forecast-Zeitabschnitt verwenden.", exception.Message);
    }

    [Fact]
    public async Task EvaluateRejectsMissingBatteryState()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(12);
        var priceForecast = await CreatePriceForecastAsync();
        var forecastSlot = new ForecastTimeSlot(decisionTimeUtc, decisionTimeUtc.AddMinutes(15));
        var pvYieldSlot = new PvYieldForecastSlot(forecastSlot, 0.40m);
        var consumptionSlot = new ConsumptionForecastSlot(forecastSlot, 0.30m);
        var evaluator = new BatteryDecisionRuleEvaluator();

        var exception = Assert.Throws<ArgumentNullException>(
            () => evaluator.Evaluate(decisionTimeUtc, priceForecast, pvYieldSlot, consumptionSlot, batteryState: null!));

        Assert.Contains("Der Akkuladestand darf nicht null sein.", exception.Message);
    }

    private static async Task<IReadOnlyList<TibberPriceForecastSlot>> CreatePriceForecastAsync()
    {
        var provider = new FakeTibberPriceForecastProvider();

        return await provider.GetPriceForecastAsync(TestDayStartsAtUtc, TestDayStartsAtUtc.AddDays(1));
    }

    private static BatteryState CreateBatteryState(decimal stateOfChargePercent)
    {
        return new BatteryState(stateOfChargePercent, TestDayStartsAtUtc);
    }
}
