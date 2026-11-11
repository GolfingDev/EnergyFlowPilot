using TibberVictronController.Api.JointBattery;
using TibberVictronController.Business.Decisions;

namespace TibberVictronController.Api.Tests;

public sealed class JointConsumptionProfileCacheTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    // 10:00 local time in Berlin (winter time).
    private static readonly DateTimeOffset BuiltAt = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TemperaturesUntil = BuiltAt.AddHours(48);

    private static ConsumptionForecastModel Model() =>
        JointConsumptionProfile.Learn([], Berlin, BuiltAt);

    private static JointConsumptionProfileCache Filled(ConsumptionForecastModel model, bool withTemperatures = true)
    {
        var cache = new JointConsumptionProfileCache();
        cache.Store(model, BuiltAt, Berlin, withTemperatures, TemperaturesUntil);
        return cache;
    }

    [Fact]
    public void ReusesTheLearnedModelWithinOneHourOfTheSameLocalDay()
    {
        var model = Model();
        var cache = Filled(model);

        Assert.Same(model, cache.TryGet(BuiltAt.AddMinutes(59), Berlin, true, TemperaturesUntil));
    }

    [Fact]
    public void RebuildsAfterOneHour()
    {
        var cache = Filled(Model());

        Assert.Null(cache.TryGet(BuiltAt + JointConsumptionProfileCache.MaximumAge, Berlin, true, TemperaturesUntil));
    }

    [Fact]
    public void RebuildsWhenTheLocalDayChanges()
    {
        // Built at 23:40 local time; 20 minutes later it is the next local day although less than an hour passed.
        var cache = new JointConsumptionProfileCache();
        var lateEvening = new DateTimeOffset(2026, 11, 11, 22, 40, 0, TimeSpan.Zero);
        cache.Store(Model(), lateEvening, Berlin, true, lateEvening.AddHours(48));

        Assert.NotNull(cache.TryGet(lateEvening.AddMinutes(19), Berlin, true, lateEvening.AddHours(48)));
        Assert.Null(cache.TryGet(lateEvening.AddMinutes(20), Berlin, true, lateEvening.AddHours(48)));
    }

    [Fact]
    public void RebuildsWhenTheTemperatureInputChangesOrDoesNotCoverTheHorizon()
    {
        var withTemperatures = Filled(Model());
        var withoutTemperatures = Filled(Model(), withTemperatures: false);
        var later = BuiltAt.AddMinutes(10);

        Assert.Null(withTemperatures.TryGet(later, Berlin, false, TemperaturesUntil));
        Assert.Null(withoutTemperatures.TryGet(later, Berlin, true, TemperaturesUntil));
        // New day-ahead prices extend the horizon: the model needs heating degrees of the additional day.
        Assert.Null(withTemperatures.TryGet(later, Berlin, true, TemperaturesUntil.AddHours(24)));
        Assert.NotNull(withoutTemperatures.TryGet(later, Berlin, false, TemperaturesUntil.AddHours(24)));
    }

    [Fact]
    public void RebuildsForAnotherTimeZoneOrAClockBeforeTheBuild()
    {
        var cache = Filled(Model());

        Assert.Null(cache.TryGet(BuiltAt.AddMinutes(10), TimeZoneInfo.Utc, true, TemperaturesUntil));
        Assert.Null(cache.TryGet(BuiltAt.AddMinutes(-1), Berlin, true, TemperaturesUntil));
    }
}
