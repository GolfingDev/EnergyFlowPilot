using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>Uses complete historical days rather than controller polling frequency or an isolated price spike.</summary>
public static class JointBatteryContinuationEstimator
{
    public static JointBatteryContinuation? Estimate(IReadOnlyList<TibberPriceForecastSlot> history,
        TimeZoneInfo zone, IReadOnlyList<(decimal ConsumptionKwh, decimal PvKwh)> followingDay)
    {
        var daily = history.Where(p => !p.IsEstimated && p.TimeSlot.IsFifteenMinuteSlot)
            .DistinctBy(p => p.TimeSlot.StartsAtUtc)
            .GroupBy(p => TimeZoneInfo.ConvertTime(p.TimeSlot.StartsAtUtc, zone).Date)
            .Where(day => {
                var start = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.Key, DateTimeKind.Unspecified), zone);
                var end = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.Key.AddDays(1), DateTimeKind.Unspecified), zone);
                // DST days have 23 or 25 hours. Sparse logging must not turn a few peak prices into a typical day.
                return day.Count() * .25 >= (end - start).TotalHours * .9;
            })
            .Select(day => day.Average(p => p.TotalPricePerKwh)).Order().ToArray();
        if (daily.Length < 3 || followingDay.Count != 96) return null;
        var median = daily.Length % 2 == 1 ? daily[daily.Length / 2] : (daily[daily.Length / 2 - 1] + daily[daily.Length / 2]) / 2;
        // Net daily demand leaves room for forecast PV. Both batteries share this single energy budget.
        var demand = Math.Max(0, followingDay.Sum(p => p.ConsumptionKwh - p.PvKwh));
        return new(Math.Max(0, median) * .85m, demand, median, daily.Length);
    }
}
