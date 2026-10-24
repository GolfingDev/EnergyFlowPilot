using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class JointBatteryContinuationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    [Fact]
    public void TypicalDailyPriceRejectsOutliersAndSparseDaysIncludingDst()
    {
        var history = new List<TibberPriceForecastSlot>();
        // Includes the 25-hour autumn day; every real quarter-hour remains distinct.
        for (var day = new DateTime(2026, 10, 20); day < new DateTime(2026, 10, 27); day = day.AddDays(1))
        {
            var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day, Zone), TimeSpan.Zero);
            var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1), Zone), TimeSpan.Zero);
            for (var at = start; at < end; at = at.AddMinutes(15))
                history.Add(new(new(at, at.AddMinutes(15)), day.Day == 21 ? 2m : .27m, "EUR"));
        }
        var sparse = Now.AddDays(-20);
        history.Add(new(new(sparse, sparse.AddMinutes(15)), 10m, "EUR"));
        var result = JointBatteryContinuationEstimator.Estimate(history, Zone,
            Enumerable.Repeat((.5m, .1m), 96).ToArray());
        Assert.NotNull(result);
        Assert.Equal(7, result.HistoricalDays);
        Assert.Equal(.27m, result.HistoricalTypicalPricePerKwh);
        Assert.Equal(.2295m, result.ValuePerDeliveredKwh);
        Assert.Equal(38.4m, result.MaximumDeliveredEnergyKwh);
    }

    [Fact]
    public void MissingHistoryOrIncompleteFutureDisablesAdditionalPurchases()
    {
        Assert.Null(JointBatteryContinuationEstimator.Estimate([], Zone, Enumerable.Repeat((.5m, 0m), 96).ToArray()));
        Assert.Null(JointBatteryContinuationEstimator.Estimate([], Zone, []));
    }

    [Fact]
    public void CheapPublishedWindowFillsBothBatteriesForFutureDemand()
    {
        var baseline = Plan(.1476m, null);
        var stocked = Plan(.1476m, new(.2295m, 30m, .27m, 7));
        Assert.All(baseline.Slots, s => Assert.All(s.Targets, t => Assert.Equal(0, t.SignedPowerWatts)));
        Assert.All(stocked.Slots[^1].Targets, t => Assert.InRange(t.SocAfterPercent, 94.9m, 95m));
        Assert.All(stocked.Slots, s => Assert.True(s.Targets[0].SignedPowerWatts * (long)s.Targets[1].SignedPowerWatts >= 0));
    }

    [Theory]
    [InlineData(.30, 90, false, 30)]
    [InlineData(.15, 50, false, 30)]
    [InlineData(.15, 90, true, 30)]
    [InlineData(.15, 90, false, 0)]
    public void NoStockpilingWhenExpensiveLossyEstimatedOrNoFutureNeed(double price, int efficiency, bool estimated, int demand)
    {
        var plan = Plan((decimal)price, new(.2295m, demand, .27m, 7), efficiency, estimated);
        Assert.All(plan.Slots, s => Assert.All(s.Targets, t => Assert.Equal(0, t.SignedPowerWatts)));
    }

    [Fact]
    public void FutureEnergyBudgetIsSharedRatherThanGrantedToEachBattery()
    {
        var plan = Plan(.1476m, new(.2295m, 2m, .27m, 7));
        var deliverable = plan.Slots[^1].Targets.Sum(t => (t.SocAfterPercent - 10m) / 100m * 10m * (decimal)Math.Sqrt(.9));
        Assert.InRange(deliverable, 1.8m, 2.15m);
    }

    private static JointBatteryPlan Plan(decimal price, JointBatteryContinuation? continuation, int efficiency = 90, bool estimated = false)
    {
        var configuration = new BatteryConfiguration(new BatteryConfigurationValues {
            TotalCapacityKwh = 10m, PlanningMinimumStateOfChargePercent = 10m, PlanningMaximumStateOfChargePercent = 95m,
            RoundTripEfficiencyPercent = efficiency, MaximumChargePowerWatts = 3000, MaximumDischargePowerWatts = 3000 });
        var times = Enumerable.Range(0, 16).Select(i => new ForecastTimeSlot(Now.AddMinutes(15 * i), Now.AddMinutes(15 * (i + 1)))).ToArray();
        return new JointBatteryPlanner().Calculate(new() {
            NowUtc = Now, Batteries = [new("victron", configuration, new(10m, Now), 0), new("e3dc", configuration, new(10m, Now), 0)],
            Prices = times.Select(t => new TibberPriceForecastSlot(t, price, "EUR") { IsEstimated = estimated }).ToArray(),
            Pv = times.Select(t => new PvYieldForecastSlot(t, 0m)).ToArray(),
            Consumption = times.Select(t => new ConsumptionForecastSlot(t, 0m)).ToArray(),
            MaximumGridImportWatts = 0, Continuation = continuation
        });
    }
}
