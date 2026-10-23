using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>Reconstructs gross house demand without learning either battery's action as consumption.</summary>
public static class JointConsumptionProfile
{
    public static decimal? GrossWatts(LiveConsumptionSample sample) => sample is {
        GridPowerWatts: { } grid, BatteryPowerWatts: { } primary,
        SupplementaryBatteryPowerWatts: { } secondary, SupplementaryPvProductionWatts: { } pv
    } ? Math.Max(0, grid - primary - secondary + pv) : null;

    public static ConsumptionForecastModel Learn(IReadOnlyList<LiveConsumptionSample> samples,
        TimeZoneInfo zone, DateTimeOffset now, IReadOnlyList<HourlyTemperature>? temperatures = null)
    {
        var averages = samples.Select(s => (Time: TimeZoneInfo.ConvertTime(s.MeasuredAtUtc, zone), Watts: GrossWatts(s)))
            .Where(s => s.Watts is >= 0 and <= 100000)
            .GroupBy(s => (Date: DateOnly.FromDateTime(s.Time.DateTime), Slot: s.Time.Hour * 4 + s.Time.Minute / 15))
            .Select(g => new ConsumptionDaySlotAverage(g.Key.Date, g.Key.Slot, g.Average(s => s.Watts!.Value))).ToArray();
        // Existing long-term summaries contain the independently controlled E3/DC effect and cannot be reused here.
        return ConsumptionForecastModel.Build(averages, temperatures is null ? new Dictionary<DateOnly, decimal>() :
            ConsumptionForecastModel.CalculateHeatingDegrees(temperatures, zone),
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime));
    }
}
