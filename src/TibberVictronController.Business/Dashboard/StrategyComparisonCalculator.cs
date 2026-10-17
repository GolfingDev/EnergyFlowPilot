using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Dashboard;

/// <summary>
/// Inputs for comparing the measured operation with the simple "battery first" strategy.
/// </summary>
public sealed class StrategyComparisonInput
{
    public required IReadOnlyList<LiveConsumptionSample> Samples { get; init; }

    /// <summary>
    /// Returns the energy price of the 15-minute slot starting at the given UTC time, or null when it is unknown.
    /// </summary>
    public required Func<DateTimeOffset, decimal?> GetSlotPricePerKwh { get; init; }

    public required BatteryConfiguration BatteryConfiguration { get; init; }

    public required decimal FeedInCompensationPricePerKwh { get; init; }

    /// <summary>
    /// Import margin the real controller keeps while discharging; the baseline keeps the same margin to stay comparable.
    /// </summary>
    public int GridPowerDeadbandWatts { get; init; }
}

public sealed record StrategyComparisonResult(
    DashboardCostComparison? Total,
    IReadOnlyDictionary<DateTimeOffset, DashboardHourCost> Hours);

public sealed record DashboardHourCost(decimal ActualCost, decimal BaselineCost);

/// <summary>
/// Replays the measured period with a simulated Victron battery that always covers import and always stores surplus,
/// without grid charging or holding. The difference to the real costs shows whether the price-based plan paid off.
/// </summary>
/// <remarks>
/// The site demand without the Victron battery is reconstructed from grid power minus the battery AC power, the same way
/// the direct decision does. The E3/DC reaction to a different Victron behaviour cannot be replayed and is assumed unchanged.
/// Different end states of charge are valued at the average price of the period, so stored energy bought for the next
/// day does not count as a loss.
/// </remarks>
public static class StrategyComparisonCalculator
{
    private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromMinutes(1);
    private static readonly long SlotTicks = TimeSpan.FromMinutes(15).Ticks;

    public static StrategyComparisonResult Calculate(StrategyComparisonInput input)
    {
        var configuration = input.BatteryConfiguration;
        var samples = input.Samples;
        var hours = new SortedDictionary<DateTimeOffset, HourAccumulator>();
        var efficiency = (decimal)Math.Sqrt((double)(configuration.RoundTripEfficiencyPercent / 100m));
        var minimumStoredKwh = configuration.TotalCapacityKwh * configuration.PlanningMinimumStateOfChargePercent / 100m;
        var maximumStoredKwh = configuration.TotalCapacityKwh * configuration.PlanningMaximumStateOfChargePercent / 100m;
        var firstSoc = samples.FirstOrDefault(sample => sample.BatterySocPercent is not null)?.BatterySocPercent;
        var lastSoc = samples.LastOrDefault(sample => sample.BatterySocPercent is not null)?.BatterySocPercent;

        if (firstSoc is null || lastSoc is null)
        {
            return new StrategyComparisonResult(null, new Dictionary<DateTimeOffset, DashboardHourCost>());
        }

        var startStoredKwh = configuration.TotalCapacityKwh * firstSoc.Value / 100m;
        var baselineStoredKwh = startStoredKwh;
        var total = new HourAccumulator();
        var totalDurationHours = 0m;

        for (var index = 0; index < samples.Count - 1; index++)
        {
            var sample = samples[index];
            var gap = samples[index + 1].MeasuredAtUtc - sample.MeasuredAtUtc;
            var durationHours = (decimal)(gap < MaximumSampleGap ? gap : MaximumSampleGap).TotalHours;
            totalDurationHours += durationHours;

            var price = input.GetSlotPricePerKwh(new DateTimeOffset(sample.MeasuredAtUtc.UtcTicks - sample.MeasuredAtUtc.UtcTicks % SlotTicks, TimeSpan.Zero));
            if (sample.GridPowerWatts is not { } gridWatts || sample.BatteryPowerWatts is not { } batteryDcWatts || price is null || durationHours <= 0m)
            {
                continue;
            }

            var batteryAcWatts = batteryDcWatts > 0m ? batteryDcWatts / efficiency : batteryDcWatts * efficiency;
            var gridWithoutBatteryWatts = gridWatts - batteryAcWatts;
            var baselineGridWatts = SimulateBaselineStep(new BaselineStep(
                gridWithoutBatteryWatts,
                input.GridPowerDeadbandWatts,
                durationHours,
                efficiency,
                configuration,
                minimumStoredKwh,
                maximumStoredKwh), ref baselineStoredKwh);

            var hourStartsAtUtc = new DateTimeOffset(sample.MeasuredAtUtc.UtcTicks - sample.MeasuredAtUtc.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);
            if (!hours.TryGetValue(hourStartsAtUtc, out var hour))
            {
                hour = new HourAccumulator();
                hours[hourStartsAtUtc] = hour;
            }

            var costs = new IntervalCosts(
                CalculateCost(gridWatts, price.Value, input.FeedInCompensationPricePerKwh, durationHours),
                CalculateCost(baselineGridWatts, price.Value, input.FeedInCompensationPricePerKwh, durationHours),
                CalculateCost(gridWithoutBatteryWatts, price.Value, input.FeedInCompensationPricePerKwh, durationHours),
                price.Value,
                durationHours);
            hour.Add(costs);
            total.Add(costs);
        }

        if (total.PricedDurationHours <= 0m)
        {
            return new StrategyComparisonResult(null, new Dictionary<DateTimeOffset, DashboardHourCost>());
        }

        var averagePrice = total.PriceTimeSum / total.PricedDurationHours;
        var actualEndStoredKwh = configuration.TotalCapacityKwh * lastSoc.Value / 100m;

        // Stored energy is worth what it replaces later: grid import at a typical price, after discharge losses.
        var storedDifferenceKwh = actualEndStoredKwh - baselineStoredKwh;
        var storedDifferenceValue = storedDifferenceKwh * efficiency * averagePrice;
        var storedChangeValue = (actualEndStoredKwh - startStoredKwh) * efficiency * averagePrice;

        var comparison = new DashboardCostComparison(
            Round(total.ActualCost),
            Round(total.BaselineCost),
            Round(total.WithoutBatteryCost),
            Round(storedDifferenceKwh),
            Round(storedDifferenceValue),
            Round(total.BaselineCost - total.ActualCost + storedDifferenceValue),
            Round(total.WithoutBatteryCost - total.ActualCost + storedChangeValue),
            Math.Round(averagePrice, 4, MidpointRounding.AwayFromZero),
            totalDurationHours <= 0m ? 0m : Math.Round(total.PricedDurationHours / totalDurationHours * 100m, 1, MidpointRounding.AwayFromZero));

        return new StrategyComparisonResult(
            comparison,
            hours.ToDictionary(hour => hour.Key, hour => new DashboardHourCost(Round(hour.Value.ActualCost), Round(hour.Value.BaselineCost))));
    }

    /// <summary>
    /// Covers import from the battery down to the planning minimum and stores export up to the planning maximum.
    /// </summary>
    /// <remarks>
    /// Like the real controller, the baseline ignores deviations inside the deadband and keeps the deadband as import margin
    /// while discharging. Without that margin the baseline would look better only because it regulates perfectly to 0 W.
    /// </remarks>
    private static decimal SimulateBaselineStep(BaselineStep step, ref decimal storedKwh)
    {
        var configuration = step.Configuration;

        if (Math.Abs(step.GridWithoutBatteryWatts) <= step.DeadbandWatts)
        {
            return step.GridWithoutBatteryWatts;
        }

        if (step.GridWithoutBatteryWatts > 0m)
        {
            var availableAcWatts = Math.Max(0m, storedKwh - step.MinimumStoredKwh) * step.Efficiency * 1000m / step.DurationHours;
            var dischargeAcWatts = Math.Min(step.GridWithoutBatteryWatts - step.DeadbandWatts, Math.Min(configuration.MaximumDischargePowerWatts, availableAcWatts));
            storedKwh -= dischargeAcWatts / step.Efficiency * step.DurationHours / 1000m;

            return step.GridWithoutBatteryWatts - dischargeAcWatts;
        }

        var headroomAcWatts = Math.Max(0m, step.MaximumStoredKwh - storedKwh) / step.Efficiency * 1000m / step.DurationHours;
        var chargeAcWatts = Math.Min(-step.GridWithoutBatteryWatts, Math.Min(configuration.MaximumChargePowerWatts, headroomAcWatts));
        storedKwh += chargeAcWatts * step.Efficiency * step.DurationHours / 1000m;

        return step.GridWithoutBatteryWatts + chargeAcWatts;
    }

    private static decimal CalculateCost(decimal gridWatts, decimal pricePerKwh, decimal feedInPricePerKwh, decimal durationHours)
    {
        var energyKwh = gridWatts * durationHours / 1000m;
        return energyKwh >= 0m ? energyKwh * pricePerKwh : energyKwh * feedInPricePerKwh;
    }

    private static decimal Round(decimal value)
    {
        return Math.Round(value, 3, MidpointRounding.AwayFromZero);
    }

    private sealed record BaselineStep(
        decimal GridWithoutBatteryWatts,
        int DeadbandWatts,
        decimal DurationHours,
        decimal Efficiency,
        BatteryConfiguration Configuration,
        decimal MinimumStoredKwh,
        decimal MaximumStoredKwh);

    private sealed record IntervalCosts(
        decimal Actual,
        decimal Baseline,
        decimal WithoutBattery,
        decimal PricePerKwh,
        decimal DurationHours);

    private sealed class HourAccumulator
    {
        public decimal ActualCost { get; private set; }

        public decimal BaselineCost { get; private set; }

        public decimal WithoutBatteryCost { get; private set; }

        public decimal PriceTimeSum { get; private set; }

        public decimal PricedDurationHours { get; private set; }

        public void Add(IntervalCosts costs)
        {
            ActualCost += costs.Actual;
            BaselineCost += costs.Baseline;
            WithoutBatteryCost += costs.WithoutBattery;
            PriceTimeSum += costs.PricePerKwh * costs.DurationHours;
            PricedDurationHours += costs.DurationHours;
        }
    }
}
