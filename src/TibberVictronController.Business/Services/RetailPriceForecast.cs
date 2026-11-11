using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Services;

/// <summary>One quarter hour with a tariff price (EUR/kWh, gross).</summary>
public sealed record RetailPricePoint(DateTimeOffset StartsAtUtc, double PricePerKwh);

/// <summary>
/// Turns the exchange price forecast (energyforecast.de, spot without fees) into tariff prices: a straight line fitted
/// to the published quarter hours that have both a tariff price and the exchange price (the tariff adds fees and VAT
/// to the spot price). Used by the car plan and the price chart, so both show the same forecast.
/// </summary>
public static class RetailPriceForecast
{
    // Fewer pairs give no reliable line; then there is no forecast.
    private const int MinimumPairs = 16;
    // A pass-through tariff adds 19 % VAT on the spot price; a fit far from that points at bad data.
    private const double MinimumSlope = 0.8;
    private const double MaximumSlope = 1.6;
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);
    // Fetched every three hours; a forecast from a stopped fetch is no longer worth planning or showing.
    private static readonly TimeSpan MaximumForecastAge = TimeSpan.FromHours(12);

    /// <summary>Published exchange prices and forecasts no older than twelve hours.</summary>
    public static IReadOnlyList<PriceForecastSnapshot> Fresh(IEnumerable<PriceForecastSnapshot> snapshots, DateTimeOffset nowUtc) =>
        snapshots.Where(snapshot => snapshot.LeadDays == PriceForecastSnapshot.Actual || nowUtc - snapshot.CapturedAtUtc <= MaximumForecastAge).ToArray();

    /// <summary>Tariff = intercept + slope x spot; null when the published prices do not allow a reliable fit.</summary>
    public static (double Intercept, double Slope)? Fit(IEnumerable<RetailPricePoint> published, IReadOnlyList<PriceForecastSnapshot> snapshots)
    {
        var actual = snapshots.Where(snapshot => snapshot.LeadDays == PriceForecastSnapshot.Actual)
            .GroupBy(snapshot => snapshot.SlotStartUtc)
            .ToDictionary(group => group.Key, group => (double)group.First().SpotPricePerKwh);
        var pairs = published.Where(point => actual.ContainsKey(point.StartsAtUtc))
            .Select(point => (Spot: actual[point.StartsAtUtc], Retail: point.PricePerKwh)).ToArray();
        if (pairs.Length < MinimumPairs) return null;
        var meanSpot = pairs.Average(pair => pair.Spot);
        var meanRetail = pairs.Average(pair => pair.Retail);
        var variance = pairs.Sum(pair => (pair.Spot - meanSpot) * (pair.Spot - meanSpot));
        if (variance < 1e-6) return null;
        var slope = pairs.Sum(pair => (pair.Spot - meanSpot) * (pair.Retail - meanRetail)) / variance;
        return slope is < MinimumSlope or > MaximumSlope ? null : (meanRetail - slope * meanSpot, slope);
    }

    /// <summary>
    /// Forecast tariff prices from <paramref name="fromUtc"/> on, quarter by quarter, until the horizon or the first
    /// quarter without a forecast: a hole would let a plan jump over hours nothing is known about. Per quarter hour the
    /// newest forecast counts (it has the shortest lead time).
    /// </summary>
    public static IReadOnlyList<RetailPricePoint> Continue(DateTimeOffset fromUtc, DateTimeOffset untilUtc, IReadOnlyList<PriceForecastSnapshot> snapshots,
        (double Intercept, double Slope) line)
    {
        var forecast = snapshots.Where(snapshot => snapshot.LeadDays >= 0 && snapshot.SlotStartUtc >= fromUtc && snapshot.SlotStartUtc < untilUtc)
            .GroupBy(snapshot => snapshot.SlotStartUtc)
            .ToDictionary(group => group.Key, group => group.MaxBy(snapshot => snapshot.CapturedAtUtc)!.SpotPricePerKwh);
        var result = new List<RetailPricePoint>();
        for (var start = fromUtc; start < untilUtc && forecast.TryGetValue(start, out var spot); start += Quarter)
            result.Add(new RetailPricePoint(start, Math.Round(line.Intercept + line.Slope * (double)spot, 4)));
        return result;
    }
}
