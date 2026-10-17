using TibberVictronController.Business.Dashboard;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class StrategyComparisonCalculatorTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly BatteryConfiguration Battery = new(10m, roundTripEfficiencyPercent: 100m);

    [Fact]
    public void GridChargeAtCheapPriceIsComparedWithBatteryFirstIncludingStoredEnergy()
    {
        // First hour at 0.10: house 1 kW, battery charges 2 kW from the grid. Second hour at 0.40: battery covers the house.
        var samples = Enumerable.Range(0, 121)
            .Select(minute => minute < 60
                ? CreateSample(minute, gridWatts: 3000m, batteryWatts: 2000m, socPercent: 50m)
                : CreateSample(minute, gridWatts: 0m, batteryWatts: -1000m, socPercent: minute == 120 ? 60m : 55m))
            .ToArray();

        var result = Calculate(samples, slotStartsAtUtc => slotStartsAtUtc < StartUtc.AddHours(1) ? 0.10m : 0.40m);

        var costs = result.Total!;
        Assert.Equal(0.30m, costs.ActualCost);
        Assert.Equal(0m, costs.BaselineCost);
        Assert.Equal(0.50m, costs.WithoutBatteryCost);
        Assert.Equal(0.25m, costs.AveragePricePerKwh);

        // Battery first ends at 3 kWh, the real operation at 6 kWh: 3 kWh more stored, worth 0.75 at the average price.
        Assert.Equal(3m, costs.StoredEnergyDifferenceKwh);
        Assert.Equal(0.75m, costs.StoredEnergyDifferenceValue);
        Assert.Equal(0.45m, costs.AdvantageOverBaseline);
        Assert.Equal(0.45m, costs.AdvantageOverNoBattery);
        Assert.Equal(100m, costs.PricedCoveragePercent);
        Assert.Equal(new DashboardHourCost(0.30m, 0m), result.Hours[StartUtc]);
    }

    [Fact]
    public void ExportedSurplusThatBatteryFirstWouldStoreCountsAsDisadvantage()
    {
        // The battery stays idle while 2 kW are exported for one hour at a price of 0.30 and 0.08 feed-in compensation.
        var samples = Enumerable.Range(0, 61)
            .Select(minute => CreateSample(minute, gridWatts: -2000m, batteryWatts: 0m, socPercent: 50m))
            .ToArray();

        var costs = Calculate(samples, _ => 0.30m).Total!;

        Assert.Equal(-0.16m, costs.ActualCost);
        Assert.Equal(0m, costs.BaselineCost);
        Assert.Equal(-2m, costs.StoredEnergyDifferenceKwh);
        Assert.Equal(-0.44m, costs.AdvantageOverBaseline);
    }

    [Fact]
    public void BatteryFirstStopsAtPlanningMinimum()
    {
        // 10 % planning minimum of 10 kWh leaves 0.5 kWh usable from 15 %; the rest of the 2 kWh demand stays grid import.
        var samples = Enumerable.Range(0, 61)
            .Select(minute => CreateSample(minute, gridWatts: 2000m, batteryWatts: 0m, socPercent: 15m))
            .ToArray();

        var costs = Calculate(samples, _ => 0.20m).Total!;

        Assert.Equal(0.40m, costs.ActualCost);
        Assert.Equal(0.30m, costs.BaselineCost);
    }

    [Fact]
    public void MissingPricesReduceCoverageAndNoPricesGiveNoComparison()
    {
        var samples = Enumerable.Range(0, 61)
            .Select(minute => CreateSample(minute, gridWatts: 500m, batteryWatts: 0m, socPercent: 50m))
            .ToArray();

        var partial = Calculate(samples, slotStartsAtUtc => slotStartsAtUtc < StartUtc.AddMinutes(30) ? 0.30m : null).Total!;
        var none = Calculate(samples, _ => null);

        Assert.Equal(50m, partial.PricedCoveragePercent);
        Assert.Null(none.Total);
        Assert.Empty(none.Hours);
    }

    private static StrategyComparisonResult Calculate(IReadOnlyList<LiveConsumptionSample> samples, Func<DateTimeOffset, decimal?> prices)
    {
        return StrategyComparisonCalculator.Calculate(new StrategyComparisonInput
        {
            Samples = samples,
            GetSlotPricePerKwh = prices,
            BatteryConfiguration = Battery,
            FeedInCompensationPricePerKwh = 0.08m
        });
    }

    private static LiveConsumptionSample CreateSample(int minute, decimal gridWatts, decimal batteryWatts, decimal socPercent)
    {
        return new LiveConsumptionSample(1000m, StartUtc.AddMinutes(minute), gridWatts, batteryWatts, socPercent, pvProductionWatts: null);
    }
}
