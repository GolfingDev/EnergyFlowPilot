using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class ConsumptionForecastModelTests
{
    private static readonly DateOnly Today = new(2026, 10, 17);
    private static readonly TimeSpan SlotDuration = TimeSpan.FromMinutes(15);

    [Fact]
    public void ProfileWeighsRecentDaysMoreThanOlderDays()
    {
        // Ten days ago 400 W, yesterday 1000 W: the half-life of three days must pull the profile clearly towards yesterday.
        var history = CreateFlatDay(Today.AddDays(-10), 400m).Concat(CreateFlatDay(Today.AddDays(-1), 1000m)).ToArray();

        var model = ConsumptionForecastModel.Build(history, new Dictionary<DateOnly, decimal>(), Today);
        var slotWatts = model.GetSlotConsumptionKwh(Today, 40, SlotDuration)!.Value * 4m * 1000m;

        Assert.InRange(slotWatts, 900m, 1000m);
        Assert.Equal(1m, model.GetTemperatureScale(Today));
    }

    [Fact]
    public void ColderForecastDayScalesConsumptionWithLearnedHeatingDependency()
    {
        // 10 kWh base load plus 1 kWh per heating degree over seven measured days.
        var heatingDegrees = new Dictionary<DateOnly, decimal>();
        var history = new List<ConsumptionDaySlotAverage>();

        for (var dayOffset = 7; dayOffset >= 1; dayOffset--)
        {
            var date = Today.AddDays(-dayOffset);
            var degrees = dayOffset;
            heatingDegrees[date] = degrees;
            history.AddRange(CreateFlatDay(date, (10m + degrees) * 1000m / 24m));
        }

        heatingDegrees[Today.AddDays(1)] = 12m;

        var model = ConsumptionForecastModel.Build(history, heatingDegrees, Today);

        Assert.Equal(1m, Math.Round(model.HeatingSlopeKwhPerDegree!.Value, 3));
        Assert.Equal(10m, Math.Round(model.BaseDailyKwh!.Value, 3));

        var tomorrowDailyKwh = Enumerable.Range(0, ConsumptionForecastModel.SlotsPerDay)
            .Sum(slot => model.GetSlotConsumptionKwh(Today.AddDays(1), slot, SlotDuration)!.Value);

        // The weighted reference lies between the measured days, so tomorrow (12 heating degrees) must exceed all of them.
        Assert.InRange(tomorrowDailyKwh, 19m, 23m);
    }

    [Fact]
    public void UncorrelatedTemperaturesKeepPlainProfile()
    {
        var heatingDegrees = new Dictionary<DateOnly, decimal>();
        var history = new List<ConsumptionDaySlotAverage>();
        var energies = new[] { 12m, 18m, 11m, 17m, 13m, 16m, 14m };

        for (var index = 0; index < energies.Length; index++)
        {
            var date = Today.AddDays(-7 + index);
            heatingDegrees[date] = index;
            history.AddRange(CreateFlatDay(date, energies[index] * 1000m / 24m));
        }

        heatingDegrees[Today.AddDays(1)] = 20m;

        var model = ConsumptionForecastModel.Build(history, heatingDegrees, Today);

        Assert.Null(model.HeatingSlopeKwhPerDegree);
        Assert.Equal(1m, model.GetTemperatureScale(Today.AddDays(1)));
    }

    [Fact]
    public void HeatingDegreesUseLocalDaysAndIgnoreWarmHours()
    {
        var berlin = ResolveBerlin();
        var localMidnightUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 16), berlin), TimeSpan.Zero);
        var temperatures = Enumerable.Range(0, 24)
            .Select(hour => new HourlyTemperature(localMidnightUtc.AddHours(hour), hour < 12 ? 5m : 20m))
            .ToArray();

        var degrees = ConsumptionForecastModel.CalculateHeatingDegrees(temperatures, berlin);

        Assert.Equal(5m, degrees[new DateOnly(2026, 10, 16)]);
    }

    [Fact]
    public void MissingSlotReturnsNullSoCallerCanFallBack()
    {
        var model = ConsumptionForecastModel.Build(Array.Empty<ConsumptionDaySlotAverage>(), new Dictionary<DateOnly, decimal>(), Today);

        Assert.False(model.HasProfile);
        Assert.Null(model.GetSlotConsumptionKwh(Today, 0, SlotDuration));
    }

    private static IEnumerable<ConsumptionDaySlotAverage> CreateFlatDay(DateOnly date, decimal watts)
    {
        return Enumerable.Range(0, ConsumptionForecastModel.SlotsPerDay)
            .Select(slot => new ConsumptionDaySlotAverage(date, slot, watts));
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
}
