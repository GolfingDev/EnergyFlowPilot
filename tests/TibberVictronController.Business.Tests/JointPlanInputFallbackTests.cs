using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class JointPlanInputFallbackTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 19, 0, 0, TimeSpan.Zero);

    private static ForecastTimeSlot Slot(int index) => new(Start.AddMinutes(15 * index), Start.AddMinutes(15 * (index + 1)));
    private static TibberPriceForecastSlot Price(int index) => new(Slot(index), 0.30m, "EUR");

    [Fact]
    public void LaterPriceGapShortensTheHorizonInsteadOfDiscardingAllPrices()
    {
        var prices = new[] { Price(0), Price(1), Price(3), Price(4) };
        var run = JointPlanInputFallback.ContiguousFrom(prices, Start, Start.AddHours(48));
        Assert.Equal(2, run.Count);
        Assert.Equal(Slot(1), run[^1].TimeSlot);
    }

    [Fact]
    public void CachedPricesAreUsedWhenFreshOnesFailOrMissTheCurrentQuarterHour()
    {
        var cached = Enumerable.Range(-4, 20).Select(Price).ToArray();
        var (fromFailure, cacheUsed) = JointPlanInputFallback.ChoosePrices(null, cached, Start, Start.AddHours(48));
        Assert.True(cacheUsed);
        Assert.Equal(Start, fromFailure[0].TimeSlot.StartsAtUtc);
        var (_, cacheUsedForLateFresh) = JointPlanInputFallback.ChoosePrices([Price(2), Price(3)], cached, Start, Start.AddHours(48));
        Assert.True(cacheUsedForLateFresh);
        var (fresh, freshUsed) = JointPlanInputFallback.ChoosePrices([Price(0), Price(1)], cached, Start, Start.AddHours(48));
        Assert.False(freshUsed);
        Assert.Equal(2, fresh.Count);
    }

    [Fact]
    public void MissingPvForecastUsesTheLastForecastAndOtherwiseZero()
    {
        // Seen on 29.10.2026: Forecast.Solar failed for over an hour and every joint plan was discarded.
        var prices = Enumerable.Range(0, 4).Select(Price).ToArray();
        var cached = new[] { new PvYieldForecastSlot(Slot(0), 0.4m), new PvYieldForecastSlot(Slot(1), 0.3m) };
        var (pv, fromCache, missing) = JointPlanInputFallback.CompletePv(prices, null, cached);
        Assert.Equal(4, pv.Count);
        Assert.Equal(2, fromCache);
        Assert.Equal(2, missing);
        Assert.Equal(0.4m, pv[0].ExpectedPvYieldKwh);
        Assert.Equal(0m, pv[3].ExpectedPvYieldKwh);
        Assert.False(pv[3].IsForecastAvailable);
    }

    [Fact]
    public void FreshPvWinsAndPlaceholdersAreNotTreatedAsForecasts()
    {
        var prices = Enumerable.Range(0, 2).Select(Price).ToArray();
        var fresh = new[] { new PvYieldForecastSlot(Slot(0), 0.5m), new PvYieldForecastSlot(Slot(1), 0m) { IsForecastAvailable = false } };
        var cached = new[] { new PvYieldForecastSlot(Slot(1), 0.2m) };
        var (pv, fromCache, missing) = JointPlanInputFallback.CompletePv(prices, fresh, cached);
        Assert.Equal(0.5m, pv[0].ExpectedPvYieldKwh);
        Assert.Equal(0.2m, pv[1].ExpectedPvYieldKwh);
        Assert.Equal(1, fromCache);
        Assert.Equal(0, missing);
    }

    [Fact]
    public void QuarterStartFollowsTheRunningQuarterHour() =>
        Assert.Equal(Start.AddMinutes(15), JointPlanInputFallback.QuarterStart(Start.AddMinutes(29).AddSeconds(59)));

    [Fact]
    public void StoredForecastFillsSlotsThatNeitherTheProviderNorTheProcessCacheCovers()
    {
        // Restart at night while Forecast.Solar fails: without the stored forecast the PV of the next day would be
        // 0 kWh and the planner could buy grid energy for a day the PV covers.
        var prices = Enumerable.Range(0, 4).Select(Price).ToArray();
        var cached = new[] { new PvYieldForecastSlot(Slot(0), 0.4m) };
        var persisted = new[] { new PvYieldForecastSlot(Slot(0), 0.9m), new PvYieldForecastSlot(Slot(1), 0.3m), new PvYieldForecastSlot(Slot(2), 0m) };

        var (pv, fromCache, fromPersisted, missing) = JointPlanInputFallback.CompletePv(prices, null, cached, persisted);

        Assert.Equal(0.4m, pv[0].ExpectedPvYieldKwh);
        Assert.Equal(0.3m, pv[1].ExpectedPvYieldKwh);
        Assert.True(pv[1].IsForecastAvailable);
        Assert.True(pv[2].IsForecastAvailable);
        Assert.False(pv[3].IsForecastAvailable);
        Assert.Equal((1, 2, 1), (fromCache, fromPersisted, missing));
    }

    [Fact]
    public void StoredForecastIsOnlyAcceptedWithABoundedAge()
    {
        var now = Start;
        var snapshots = new[]
        {
            new PvForecastSnapshot(Slot(0).StartsAtUtc, 0.5m) { CapturedAtUtc = now.AddHours(-3) },
            new PvForecastSnapshot(Slot(1).StartsAtUtc, 0.6m) { CapturedAtUtc = now - JointPlanInputFallback.MaximumPersistedPvAge },
            new PvForecastSnapshot(Slot(2).StartsAtUtc, 0.7m) { CapturedAtUtc = now - JointPlanInputFallback.MaximumPersistedPvAge - TimeSpan.FromMinutes(1) },
            new PvForecastSnapshot(Slot(3).StartsAtUtc, 0.8m),                                        // age unknown
            new PvForecastSnapshot(Slot(4).StartsAtUtc, 0.9m) { CapturedAtUtc = now.AddHours(2) }     // captured "in the future"
        };

        var usable = JointPlanInputFallback.UsablePersistedPv(snapshots, now);

        Assert.Equal([Slot(0), Slot(1)], usable.Select(p => p.TimeSlot));
        Assert.Equal(0.5m, usable[0].ExpectedPvYieldKwh);
        Assert.Empty(JointPlanInputFallback.UsablePersistedPv(null, now));
    }
}
