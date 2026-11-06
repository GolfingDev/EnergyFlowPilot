using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Services;

/// <summary>
/// How well the forecasts of one lead time matched the published prices: mean absolute error and bias in ct/kWh, and
/// for whole days what charging in the forecast's cheapest four hours cost compared to the really cheapest four.
/// </summary>
public sealed record PriceForecastAccuracyRow(
    int LeadDays,
    int Quarters,
    int Days,
    double MeanAbsoluteErrorCent,
    double BiasCent,
    double? CheapestHoursExtraCostCent);

/// <summary>
/// Shadow evaluation of the price forecast (operator 2026-11-06): before the car planning relies on days beyond the
/// auction, it is measured on own data how far ahead the forecast is good enough.
/// </summary>
public static class PriceForecastAccuracy
{
    // Four hours: about what one car charge at the wallbox takes.
    public const int CheapestQuarters = 16;
    // A day counts for the cheapest-hours check only when (almost) all of its quarter hours were forecast.
    private const int MinimumQuartersPerDay = 90;

    public static IReadOnlyList<PriceForecastSnapshot> Snapshots(IEnumerable<PriceForecastPoint> points, DateTimeOffset capturedAtUtc) =>
        points.Select(point => new PriceForecastSnapshot(point.SlotStartUtc,
            point.IsForecast ? Math.Max(0, (int)Math.Floor((point.SlotStartUtc - capturedAtUtc).TotalDays)) : PriceForecastSnapshot.Actual,
            point.SpotPricePerKwh, capturedAtUtc)).ToArray();

    public static IReadOnlyList<PriceForecastAccuracyRow> Evaluate(IReadOnlyList<PriceForecastSnapshot> snapshots, TimeZoneInfo zone)
    {
        var actual = snapshots.Where(snapshot => snapshot.LeadDays == PriceForecastSnapshot.Actual)
            .GroupBy(snapshot => snapshot.SlotStartUtc)
            .ToDictionary(group => group.Key, group => (double)group.MaxBy(snapshot => snapshot.CapturedAtUtc)!.SpotPricePerKwh);
        return snapshots.Where(snapshot => snapshot.LeadDays >= 0 && actual.ContainsKey(snapshot.SlotStartUtc))
            .GroupBy(snapshot => snapshot.LeadDays)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var errors = group.Select(snapshot => ((double)snapshot.SpotPricePerKwh - actual[snapshot.SlotStartUtc]) * 100).ToArray();
                var days = group.GroupBy(snapshot => TimeZoneInfo.ConvertTime(snapshot.SlotStartUtc, zone).Date)
                    .Where(day => day.Count() >= MinimumQuartersPerDay).ToArray();
                double? extraCost = days.Length == 0 ? null : days.Average(day =>
                {
                    var chosen = day.OrderBy(snapshot => snapshot.SpotPricePerKwh).Take(CheapestQuarters).Average(snapshot => actual[snapshot.SlotStartUtc]);
                    var best = day.Select(snapshot => actual[snapshot.SlotStartUtc]).Order().Take(CheapestQuarters).Average();
                    return (chosen - best) * 100;
                });
                return new PriceForecastAccuracyRow(group.Key, errors.Length, days.Length, errors.Average(Math.Abs), errors.Average(), extraCost);
            })
            .ToArray();
    }
}
