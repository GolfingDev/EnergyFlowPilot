using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class EvPriceForecastExtensionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset HorizonEnd = Now.AddHours(36);

    [Fact]
    public void ForecastAfterThePublishedPricesIsConvertedToTheTariffAndMarkedEstimated()
    {
        // Tariff = (spot + 0.15) x 1.19 on the published slots; the forecast continues gap-free after them.
        var published = Published(52, spot: Spot);
        var snapshots = Actual(52).Concat(Forecasts(52, 96, _ => 0.05m)).ToArray();

        var result = EvPriceForecastExtension.Extend(published, snapshots, HorizonEnd);

        Assert.Equal(Now.AddHours(13), result.ForecastFromUtc);
        var added = result.Slots.Skip(52).ToArray();
        Assert.Equal(92, added.Length);
        Assert.All(added, slot => Assert.True(slot.IsPriceEstimated));
        Assert.All(added, slot => Assert.Equal((0.05 + 0.15) * 1.19, slot.PricePerKwh, 3));
        Assert.Equal(HorizonEnd, added[^1].EndsAtUtc);
    }

    [Fact]
    public void AGapInTheForecastEndsTheExtension()
    {
        var snapshots = Actual(52).Concat(Forecasts(52, 8, _ => 0.05m)).Concat(Forecasts(64, 8, _ => 0.05m)).ToArray();

        var result = EvPriceForecastExtension.Extend(Published(52, Spot), snapshots, HorizonEnd);

        Assert.Equal(60, result.Slots.Count);
    }

    [Fact]
    public void WithoutEnoughPublishedPricesToFitNothingIsAdded()
    {
        var snapshots = Actual(8).Concat(Forecasts(52, 96, _ => 0.05m)).ToArray();

        var result = EvPriceForecastExtension.Extend(Published(52, Spot), snapshots, HorizonEnd);

        Assert.Equal(52, result.Slots.Count);
        Assert.Null(result.ForecastFromUtc);
    }

    [Fact]
    public void TheNewestForecastOfAQuarterHourIsUsed()
    {
        var older = new PriceForecastSnapshot(Now.AddHours(13), 1, 0.20m, Now.AddDays(-1));
        var newer = new PriceForecastSnapshot(Now.AddHours(13), 0, 0.05m, Now.AddHours(-1));

        var result = EvPriceForecastExtension.Extend(Published(52, Spot), [.. Actual(52), older, newer], HorizonEnd);

        Assert.Equal((0.05 + 0.15) * 1.19, Assert.Single(result.Slots.Skip(52)).PricePerKwh, 3);
    }

    private static decimal Spot(int quarter) => 0.06m + quarter % 8 * 0.01m;

    private static IReadOnlyList<EvChargeSlotInput> Published(int quarters, Func<int, decimal> spot) =>
        Enumerable.Range(0, quarters)
            .Select(index => new EvChargeSlotInput(Now.AddMinutes(15 * index), Now.AddMinutes(15 * (index + 1)), (double)(spot(index) + 0.15m) * 1.19, false, 0))
            .ToArray();

    private static IEnumerable<PriceForecastSnapshot> Actual(int quarters) =>
        Enumerable.Range(0, quarters).Select(index => new PriceForecastSnapshot(Now.AddMinutes(15 * index), PriceForecastSnapshot.Actual, Spot(index), Now));

    private static IEnumerable<PriceForecastSnapshot> Forecasts(int first, int count, Func<int, decimal> spot) =>
        Enumerable.Range(first, count).Select(index => new PriceForecastSnapshot(Now.AddMinutes(15 * index), 0, spot(index), Now));
}
