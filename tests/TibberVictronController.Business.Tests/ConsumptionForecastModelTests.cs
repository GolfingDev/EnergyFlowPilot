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
    public void StoredDaysTeachTemperatureDependencyWhenRecentWeeksAreEquallyMild()
    {
        // The last week was uniformly mild (5 heating degrees, 30 kWh). Stored winter and summer days from earlier in the
        // year carry the dependency: 10 kWh base load plus 4 kWh per heating degree.
        var heatingDegrees = new Dictionary<DateOnly, decimal>();
        var history = new List<ConsumptionDaySlotAverage>();
        for (var dayOffset = 7; dayOffset >= 1; dayOffset--)
        {
            var date = Today.AddDays(-dayOffset);
            heatingDegrees[date] = 5m;
            history.AddRange(CreateFlatDay(date, 30m * 1000m / 24m));
        }

        var storedDays = new[]
        {
            new ConsumptionDailySummary(new DateOnly(2026, 1, 15), 70m, 15m),
            new ConsumptionDailySummary(new DateOnly(2026, 2, 10), 58m, 12m),
            new ConsumptionDailySummary(new DateOnly(2026, 4, 20), 26m, 4m),
            new ConsumptionDailySummary(new DateOnly(2026, 7, 1), 10m, 0m),
            new ConsumptionDailySummary(new DateOnly(2026, 8, 5), 10m, 0m)
        };
        heatingDegrees[Today.AddDays(1)] = 15m;

        var withoutStoredDays = ConsumptionForecastModel.Build(history, heatingDegrees, Today);
        var withStoredDays = ConsumptionForecastModel.Build(history, heatingDegrees, Today, storedDays);

        Assert.Null(withoutStoredDays.HeatingSlopeKwhPerDegree);
        Assert.InRange(withStoredDays.HeatingSlopeKwhPerDegree!.Value, 3.8m, 4.2m);

        // A frosty day needs 70 kWh instead of the learned 30 kWh; the scale is capped at 2.0.
        Assert.Equal(2.0m, withStoredDays.GetTemperatureScale(Today.AddDays(1)));
    }

    [Fact]
    public void SummarizeCompleteDaysSkipsTodayAndIncompleteDays()
    {
        var heatingDegrees = new Dictionary<DateOnly, decimal>
        {
            [Today.AddDays(-2)] = 3m,
            [Today.AddDays(-1)] = 4m,
            [Today] = 5m
        };
        var history = CreateFlatDay(Today.AddDays(-2), 1000m)
            .Concat(CreateFlatDay(Today.AddDays(-1), 500m).Take(40))
            .Concat(CreateFlatDay(Today, 800m))
            .ToArray();

        var summaries = ConsumptionForecastModel.SummarizeCompleteDays(history, heatingDegrees, Today);

        var day = Assert.Single(summaries);
        Assert.Equal(new ConsumptionDailySummary(Today.AddDays(-2), 24m, 3m, 96), day);
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
