using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Tests.TestDoubles;

namespace TibberVictronController.Business.Tests;

public sealed class TibberPriceDecisionRuleTests
{
    private static readonly DateTimeOffset TestDayStartsAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EvaluateChargesFromGridDuringCheapTibberPricePhase()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(12);
        var priceForecast = await CreatePriceForecastAsync();
        var batteryState = CreateBatteryState(55m);
        var rule = new TibberPriceDecisionRule();

        var result = rule.Evaluate(decisionTimeUtc, priceForecast, batteryState);

        Assert.Equal(BatteryDecisionState.Charge, result.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.Grid, result.Instruction.ChargeSource);
        Assert.Contains(result.Reasons, reason => reason.Message.Contains("guenstigen Tibber-Preisphase"));
    }

    [Fact]
    public async Task EvaluateDischargesDuringExpensiveTibberPricePhase()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(18);
        var priceForecast = await CreatePriceForecastAsync();
        var batteryState = CreateBatteryState(55m);
        var rule = new TibberPriceDecisionRule();

        var result = rule.Evaluate(decisionTimeUtc, priceForecast, batteryState);

        Assert.Equal(BatteryDecisionState.Discharge, result.Instruction.DecisionState);
        Assert.Null(result.Instruction.ChargeSource);
        Assert.Contains(result.Reasons, reason => reason.Message.Contains("teuren Tibber-Preisphase"));
    }

    [Fact]
    public async Task EvaluateStaysIdleDuringNeutralTibberPricePhase()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(21);
        var priceForecast = await CreatePriceForecastAsync();
        var batteryState = CreateBatteryState(55m);
        var rule = new TibberPriceDecisionRule();

        var result = rule.Evaluate(decisionTimeUtc, priceForecast, batteryState);

        Assert.Equal(BatteryDecisionState.Idle, result.Instruction.DecisionState);
        Assert.Null(result.Instruction.ChargeSource);
        Assert.Contains(result.Reasons, reason => reason.Message.Contains("neutralen Tibber-Preisphase"));
    }

    [Fact]
    public void EvaluateReturnsIdleWithReasonWhenPriceForecastIsEmpty()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(12);
        var batteryState = CreateBatteryState(55m);
        var rule = new TibberPriceDecisionRule();

        var result = rule.Evaluate(decisionTimeUtc, Array.Empty<TibberPriceForecastSlot>(), batteryState);

        Assert.Equal(BatteryDecisionState.Idle, result.Instruction.DecisionState);
        Assert.Contains(result.Reasons, reason => reason.Message.Contains("Es liegen keine Tibber-Preise vor."));
    }

    [Fact]
    public async Task EvaluateRejectsNonUtcDecisionTime()
    {
        var decisionTimeWithLocalOffset = new DateTimeOffset(2026, 5, 1, 14, 0, 0, TimeSpan.FromHours(2));
        var priceForecast = await CreatePriceForecastAsync();
        var batteryState = CreateBatteryState(55m);
        var rule = new TibberPriceDecisionRule();

        var exception = Assert.Throws<ArgumentException>(
            () => rule.Evaluate(decisionTimeWithLocalOffset, priceForecast, batteryState));

        Assert.Contains("Der Entscheidungszeitpunkt muss in UTC angegeben sein.", exception.Message);
    }

    [Fact]
    public async Task EvaluateDoesNotDischargeWhenBatteryIsEmpty()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(18);
        var priceForecast = await CreatePriceForecastAsync();
        var emptyBatteryState = CreateBatteryState(0m);
        var rule = new TibberPriceDecisionRule();

        var result = rule.Evaluate(decisionTimeUtc, priceForecast, emptyBatteryState);

        Assert.Equal(BatteryDecisionState.Idle, result.Instruction.DecisionState);
        Assert.Contains(result.Reasons, reason => reason.Message.Contains("Akku ist leer"));
    }

    [Fact]
    public async Task EvaluateDoesNotChargeWhenBatteryIsFull()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(12);
        var priceForecast = await CreatePriceForecastAsync();
        var fullBatteryState = CreateBatteryState(100m);
        var rule = new TibberPriceDecisionRule();

        var result = rule.Evaluate(decisionTimeUtc, priceForecast, fullBatteryState);

        Assert.Equal(BatteryDecisionState.Idle, result.Instruction.DecisionState);
        Assert.Contains(result.Reasons, reason => reason.Message.Contains("Akku ist voll"));
    }

    [Fact]
    public async Task EvaluateChargesDuringNeutralPricePhaseWhenBatteryStateOfChargeIsLow()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(21);
        var priceForecast = await CreatePriceForecastAsync();
        var lowBatteryState = CreateBatteryState(15m);
        var rule = new TibberPriceDecisionRule();

        var result = rule.Evaluate(decisionTimeUtc, priceForecast, lowBatteryState);

        Assert.Equal(BatteryDecisionState.Charge, result.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.Grid, result.Instruction.ChargeSource);
        Assert.Contains(result.Reasons, reason => reason.Message.Contains("niedrige Akkuladestand"));
    }

    [Fact]
    public async Task EvaluateDoesNotChargeDuringOnlyNormallyCheapPricePhaseWhenBatteryStateOfChargeIsHigh()
    {
        var decisionTimeUtc = TestDayStartsAtUtc;
        var priceForecast = await CreatePriceForecastAsync();
        var highBatteryState = CreateBatteryState(90m);
        var rule = new TibberPriceDecisionRule();

        var result = rule.Evaluate(decisionTimeUtc, priceForecast, highBatteryState);

        Assert.Equal(BatteryDecisionState.Idle, result.Instruction.DecisionState);
        Assert.Contains(result.Reasons, reason => reason.Message.Contains("hohem Akkuladestand"));
    }

    [Fact]
    public async Task EvaluateChargesDuringNegativeTibberPriceEvenWhenBatteryStateOfChargeIsHigh()
    {
        var decisionTimeUtc = TestDayStartsAtUtc.AddHours(12);
        var priceForecast = await CreatePriceForecastWithNegativeMiddayPricesAsync();
        var highBatteryState = CreateBatteryState(90m);
        var rule = new TibberPriceDecisionRule();

        var result = rule.Evaluate(decisionTimeUtc, priceForecast, highBatteryState);

        Assert.Equal(BatteryDecisionState.Charge, result.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.Grid, result.Instruction.ChargeSource);
        Assert.Contains(result.Reasons, reason => reason.Message.Contains("ist negativ"));
    }

    private static async Task<IReadOnlyList<TibberPriceForecastSlot>> CreatePriceForecastAsync()
    {
        var provider = new FakeTibberPriceForecastProvider();

        return await provider.GetPriceForecastAsync(TestDayStartsAtUtc, TestDayStartsAtUtc.AddDays(1));
    }

    private static async Task<IReadOnlyList<TibberPriceForecastSlot>> CreatePriceForecastWithNegativeMiddayPricesAsync()
    {
        var priceForecast = await CreatePriceForecastAsync();

        return priceForecast
            .Select(priceSlot =>
            {
                var price = priceSlot.TimeSlot.StartsAtUtc.Hour is >= 12 and <= 15
                    ? -0.04m
                    : priceSlot.TotalPricePerKwh;

                return new TibberPriceForecastSlot(priceSlot.TimeSlot, price, priceSlot.Currency);
            })
            .ToArray();
    }

    private static BatteryState CreateBatteryState(decimal stateOfChargePercent)
    {
        return new BatteryState(stateOfChargePercent, TestDayStartsAtUtc);
    }
}
