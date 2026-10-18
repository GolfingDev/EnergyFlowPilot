using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class WeatherPriceEstimatorTests
{
    private static readonly TimeZoneInfo Berlin = ResolveBerlin();
    private static readonly DateTimeOffset HistoryStartUtc = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TodayStartUtc = new(2026, 10, 18, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FitRecoversWindAndSolarEffect()
    {
        var data = CreateData(DateTimeOffset.UtcNow);

        var model = WeatherPriceEstimator.Fit(data, Berlin)!;

        Assert.InRange(model.WindCoefficient, -155d, -145d);

        // Irradiance follows the time of day, so the hour dummies share part of its effect and the ridge shrinks it slightly.
        Assert.InRange(model.SolarCoefficient, -105d, -85d);
        Assert.InRange(model.Predict(new PriceDriverHour(TodayStartUtc.AddHours(30), 1m, 0m), Berlin), 45d, 55d);
    }

    [Fact]
    public async Task RefineAsyncReplacesOnlyEstimatedSlotsWithCalibratedTibberPrice()
    {
        // Tibber price = 0.20 EUR/kWh fees + 0.00119 x spot (EUR/MWh incl. VAT); the first day is published, the second estimated.
        var data = CreateData(new DateTimeOffset(2026, 10, 18, 18, 0, 0, TimeSpan.Zero));
        var slots = Enumerable.Range(0, 192)
            .Select(index =>
            {
                var startsAtUtc = TodayStartUtc.AddMinutes(15 * index);
                var isEstimated = index >= 96;
                var price = isEstimated ? 0.30m : 0.20m + 0.00119m * Spot(Wind(startsAtUtc), Solar(startsAtUtc));
                return new TibberPriceForecastSlot(new ForecastTimeSlot(startsAtUtc, startsAtUtc.AddMinutes(15)), price, "EUR") { IsEstimated = isEstimated };
            })
            .ToArray();

        var refined = await new WeatherPriceEstimator(new FixedPriceDriverProvider(data), Berlin).RefineAsync(slots);

        Assert.Equal(slots.Take(96).Select(slot => slot.TotalPricePerKwh), refined.Take(96).Select(slot => slot.TotalPricePerKwh));
        var windySlot = refined[96 + 4 * 3];
        var expected = 0.20m + 0.00119m * Spot(Wind(windySlot.TimeSlot.StartsAtUtc), Solar(windySlot.TimeSlot.StartsAtUtc));
        Assert.True(windySlot.IsEstimated);
        Assert.InRange(windySlot.TotalPricePerKwh, expected - 0.005m, expected + 0.005m);
    }

    [Fact]
    public async Task RefineAsyncKeepsComparableDayEstimateWithoutInputs()
    {
        var slots = new[]
        {
            new TibberPriceForecastSlot(new ForecastTimeSlot(TodayStartUtc, TodayStartUtc.AddMinutes(15)), 0.30m, "EUR") { IsEstimated = true }
        };

        var refined = await new WeatherPriceEstimator(new FixedPriceDriverProvider(null), Berlin).RefineAsync(slots);

        Assert.Same(slots, refined);
    }

    private static PriceDriverData CreateData(DateTimeOffset loadedAtUtc)
    {
        var weather = new List<PriceDriverHour>();
        var spot = new List<SpotPriceSlot>();

        for (var hour = HistoryStartUtc; hour < TodayStartUtc.AddDays(3); hour = hour.AddHours(1))
        {
            weather.Add(new PriceDriverHour(hour, Wind(hour), Solar(hour)));
            if (hour < TodayStartUtc.AddDays(1))
            {
                spot.Add(new SpotPriceSlot(hour, Spot(Wind(hour), Solar(hour))));
            }
        }

        return new PriceDriverData(weather, spot, loadedAtUtc);
    }

    private static decimal Spot(decimal wind, decimal solar)
    {
        return 200m - 150m * wind - 100m * solar;
    }

    private static decimal Wind(DateTimeOffset hour)
    {
        // Wind fronts pass every few days and are independent of the time of day.
        return (decimal)((Math.Sin((hour - HistoryStartUtc).TotalHours / 37d) + 1d) / 2d);
    }

    private static decimal Solar(DateTimeOffset hour)
    {
        var local = TimeZoneInfo.ConvertTime(hour, Berlin);
        var daylight = Math.Max(0d, Math.Sin((local.Hour - 6) / 12d * Math.PI));
        var cloudiness = (Math.Cos((hour - HistoryStartUtc).TotalHours / 53d) + 1d) / 2d;
        return (decimal)(daylight * (0.3d + 0.5d * cloudiness));
    }

    private static TimeZoneInfo ResolveBerlin()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
    }

    private sealed class FixedPriceDriverProvider : IPriceDriverProvider
    {
        private readonly PriceDriverData? data;

        public FixedPriceDriverProvider(PriceDriverData? data)
        {
            this.data = data;
        }

        public Task<PriceDriverData?> GetPriceDriversAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(data);
        }
    }
}
