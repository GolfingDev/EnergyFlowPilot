using TibberVictronController.Business.Tests.TestDoubles;

namespace TibberVictronController.Business.Tests;

public sealed class ForecastInputTestDoublesTests
{
    private static readonly DateTimeOffset TestDayStartsAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ForecastInputProvidersReturnAlignedFifteenMinuteSlotsForOneDay()
    {
        var testDayEndsAtUtc = TestDayStartsAtUtc.AddDays(1);
        var tibberPriceProvider = new FakeTibberPriceForecastProvider();
        var weatherForecastProvider = new FakeWeatherForecastProvider();
        var historicalConsumptionProvider = new FakeHistoricalConsumptionProvider();
        var batteryStateProvider = new FakeBatteryStateProvider();

        var tibberPriceSlots = await tibberPriceProvider.GetPriceForecastAsync(TestDayStartsAtUtc, testDayEndsAtUtc);
        var pvYieldSlots = await weatherForecastProvider.GetPvYieldForecastAsync(TestDayStartsAtUtc, testDayEndsAtUtc);
        var consumptionSlots = await historicalConsumptionProvider.GetConsumptionForecastAsync(TestDayStartsAtUtc, testDayEndsAtUtc);
        var batteryState = await batteryStateProvider.GetCurrentBatteryStateAsync();

        Assert.Equal(96, tibberPriceSlots.Count);
        Assert.Equal(96, pvYieldSlots.Count);
        Assert.Equal(96, consumptionSlots.Count);
        Assert.Equal(55m, batteryState.StateOfChargePercent);
        Assert.Equal(TimeSpan.Zero, batteryState.MeasuredAtUtc.Offset);

        Assert.All(tibberPriceSlots, priceSlot => Assert.True(priceSlot.TimeSlot.IsFifteenMinuteSlot));
        Assert.Equal(tibberPriceSlots.Select(slot => slot.TimeSlot), pvYieldSlots.Select(slot => slot.TimeSlot));
        Assert.Equal(tibberPriceSlots.Select(slot => slot.TimeSlot), consumptionSlots.Select(slot => slot.TimeSlot));
    }

    [Fact]
    public async Task FakeTibberPriceForecastProviderContainsCheapMiddayAndExpensiveEveningPrices()
    {
        var provider = new FakeTibberPriceForecastProvider();
        var testDayEndsAtUtc = TestDayStartsAtUtc.AddDays(1);

        var priceSlots = await provider.GetPriceForecastAsync(TestDayStartsAtUtc, testDayEndsAtUtc);

        var middayPrice = priceSlots.Single(slot => slot.TimeSlot.StartsAtUtc.Hour == 12 && slot.TimeSlot.StartsAtUtc.Minute == 0).TotalPricePerKwh;
        var eveningPrice = priceSlots.Single(slot => slot.TimeSlot.StartsAtUtc.Hour == 18 && slot.TimeSlot.StartsAtUtc.Minute == 0).TotalPricePerKwh;

        Assert.True(middayPrice < eveningPrice);
        Assert.All(priceSlots, priceSlot => Assert.Equal("EUR", priceSlot.Currency));
    }

    [Fact]
    public async Task FakeWeatherForecastProviderContainsDaylightPvYieldAndZeroNightYield()
    {
        var provider = new FakeWeatherForecastProvider();
        var testDayEndsAtUtc = TestDayStartsAtUtc.AddDays(1);

        var pvYieldSlots = await provider.GetPvYieldForecastAsync(TestDayStartsAtUtc, testDayEndsAtUtc);

        var nightYield = pvYieldSlots.Single(slot => slot.TimeSlot.StartsAtUtc.Hour == 2 && slot.TimeSlot.StartsAtUtc.Minute == 0).ExpectedPvYieldKwh;
        var middayYield = pvYieldSlots.Single(slot => slot.TimeSlot.StartsAtUtc.Hour == 12 && slot.TimeSlot.StartsAtUtc.Minute == 0).ExpectedPvYieldKwh;
        var totalYield = pvYieldSlots.Sum(slot => slot.ExpectedPvYieldKwh);

        Assert.Equal(0m, nightYield);
        Assert.True(middayYield > 0m);
        Assert.Equal(12.00m, totalYield);
    }

    [Fact]
    public async Task FakeHistoricalConsumptionProviderModelsTwentyFourKwhAverageDayForThreeParties()
    {
        var provider = new FakeHistoricalConsumptionProvider();
        var testDayEndsAtUtc = TestDayStartsAtUtc.AddDays(1);

        var consumptionSlots = await provider.GetConsumptionForecastAsync(TestDayStartsAtUtc, testDayEndsAtUtc);

        var totalConsumption = consumptionSlots.Sum(slot => slot.ExpectedConsumptionKwh);
        var morningPeakConsumption = consumptionSlots.Single(slot => slot.TimeSlot.StartsAtUtc.Hour == 7 && slot.TimeSlot.StartsAtUtc.Minute == 0).ExpectedConsumptionKwh;
        var nightConsumption = consumptionSlots.Single(slot => slot.TimeSlot.StartsAtUtc.Hour == 2 && slot.TimeSlot.StartsAtUtc.Minute == 0).ExpectedConsumptionKwh;

        Assert.Equal(24.00m, totalConsumption);
        Assert.True(morningPeakConsumption > nightConsumption);
    }
}
