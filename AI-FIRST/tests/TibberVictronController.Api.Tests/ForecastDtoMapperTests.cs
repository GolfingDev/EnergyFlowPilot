using TibberVictronController.Api.Forecast;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

public sealed class ForecastDtoMapperTests
{
    private static readonly DateTimeOffset StartsAtUtc = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MapConvertsForecastDomainModelToFrontendDto()
    {
        var timeSlot = new ForecastTimeSlot(StartsAtUtc, StartsAtUtc.AddMinutes(15));
        var decision = new CurrentBatteryDecision(
            new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
            targetPowerWatts: 1200);
        var entry = new BatteryForecastEntry(
            timeSlot,
            TibberPricePerKwh: 0.1234m,
            TibberPriceCurrency: "EUR",
            ExpectedPvYieldKwh: 0.2m,
            ExpectedConsumptionKwh: 0.1m,
            ExpectedGridImportBeforeBatteryKwh: -0.1m,
            StateOfChargeBeforePercent: 55m,
            StateOfChargeAfterPercent: 58m,
            decision,
            new[] { new BatteryDecisionReason("TestRule", "Testbegruendung") });
        var result = new BatteryForecastResult(
            new BatteryState(55m, StartsAtUtc),
            new BatteryConfiguration(10m),
            new[] { entry });

        var dto = BatteryForecastDtoMapper.Map(result);

        Assert.Equal(55m, dto.InitialStateOfChargePercent);
        Assert.Equal(10m, dto.BatteryTotalCapacityKwh);
        Assert.Single(dto.Entries);
        Assert.Equal("Charge", dto.Entries[0].DecisionState);
        Assert.Equal("Grid", dto.Entries[0].ChargeSource);
        Assert.Equal(1200, dto.Entries[0].TargetPowerWatts);
        Assert.Equal("EUR", dto.Entries[0].TibberPriceCurrency);
        Assert.Equal("TestRule", dto.Entries[0].Reasons[0].RuleName);
    }
}
