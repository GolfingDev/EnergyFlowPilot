using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class EvCheapPriceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThresholdIsThePercentileOfTheQuarterHourPrices()
    {
        // 10 days, every day the same 96 prices from 0.10 to 0.575 EUR.
        var history = Prices(10, index => 0.10m + index % 96 * 0.005m);

        var threshold = EvCheapPrice.Threshold(history, 25);

        Assert.Equal(0.215, threshold.PricePerKwh!.Value, 3);
        Assert.Equal(10, threshold.HistoryDays);
        Assert.Null(threshold.Problem);
    }

    [Fact]
    public void ShortHistoryGivesNoThreshold()
    {
        var threshold = EvCheapPrice.Threshold(Prices(3, _ => 0.20m), 25);

        Assert.Null(threshold.PricePerKwh);
        Assert.Contains("3 Tage", threshold.Problem);
    }

    private static List<TibberPriceForecastSlot> Prices(int days, Func<int, decimal> price) =>
        Enumerable.Range(0, days * 96)
            .Select(index => new TibberPriceForecastSlot(new ForecastTimeSlot(Start.AddMinutes(15 * index), Start.AddMinutes(15 * (index + 1))), price(index), "EUR"))
            .ToList();
}
