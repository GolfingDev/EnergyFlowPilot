using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

/// <summary>Car slots with forecast prices appended, and from when the prices are only forecast.</summary>
public sealed record EvForecastExtension(IReadOnlyList<EvChargeSlotInput> Slots, DateTimeOffset? ForecastFromUtc);

/// <summary>
/// Appends exchange price forecasts (energyforecast.de) after the published prices, so the car plan can judge whether
/// waiting for the night pays off before its prices are out (operator 2026-11-11: waiting may already have missed the
/// cheapest hours). The forecast is a spot price; it becomes a retail price by a straight line fitted to the published
/// slots that have both (the tariff adds fees and VAT to the spot price). Forecast slots are marked estimated, so the
/// planner adds its safety hurdle; they carry no PV, the PV forecast reaches only as far as the battery plan.
/// </summary>
public static class EvPriceForecastExtension
{
    // Fewer pairs give no reliable line; the fit is then skipped and the plan stays on published prices.
    private const int MinimumPairs = 16;
    // A pass-through tariff adds 19 % VAT on the spot price; a fit far from that points at bad data.
    private const double MinimumSlope = 0.8;
    private const double MaximumSlope = 1.6;
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);

    public static EvForecastExtension Extend(IReadOnlyList<EvChargeSlotInput> slots, IReadOnlyList<PriceForecastSnapshot> snapshots, DateTimeOffset horizonEndUtc)
    {
        if (slots.Count == 0 || snapshots.Count == 0) return new(slots, null);
        var end = slots.Max(slot => slot.EndsAtUtc);
        if (end >= horizonEndUtc) return new(slots, null);
        if (Fit(slots, snapshots) is not { } line) return new(slots, null);

        // The newest forecast per quarter hour (it has the shortest lead time).
        var forecast = snapshots.Where(snapshot => snapshot.LeadDays >= 0 && snapshot.SlotStartUtc >= end && snapshot.SlotStartUtc < horizonEndUtc)
            .GroupBy(snapshot => snapshot.SlotStartUtc)
            .ToDictionary(group => group.Key, group => group.MaxBy(snapshot => snapshot.CapturedAtUtc)!.SpotPricePerKwh);
        var added = new List<EvChargeSlotInput>();
        // Only a gap-free continuation: a hole would let the plan jump over hours it knows nothing about.
        for (var start = end; start < horizonEndUtc && forecast.TryGetValue(start, out var spot); start += Quarter)
            added.Add(new EvChargeSlotInput(start, start + Quarter, Math.Round(line.Intercept + line.Slope * (double)spot, 4), true, 0));
        return added.Count == 0 ? new(slots, null) : new([.. slots, .. added], end);
    }

    /// <summary>Retail = intercept + slope x spot, fitted on published slots with a known exchange price.</summary>
    public static (double Intercept, double Slope)? Fit(IReadOnlyList<EvChargeSlotInput> slots, IReadOnlyList<PriceForecastSnapshot> snapshots)
    {
        var actual = snapshots.Where(snapshot => snapshot.LeadDays == PriceForecastSnapshot.Actual)
            .GroupBy(snapshot => snapshot.SlotStartUtc)
            .ToDictionary(group => group.Key, group => (double)group.First().SpotPricePerKwh);
        var pairs = slots.Where(slot => !slot.IsPriceEstimated && actual.ContainsKey(slot.StartsAtUtc))
            .Select(slot => (Spot: actual[slot.StartsAtUtc], Retail: slot.PricePerKwh)).ToArray();
        if (pairs.Length < MinimumPairs) return null;
        var meanSpot = pairs.Average(pair => pair.Spot);
        var meanRetail = pairs.Average(pair => pair.Retail);
        var variance = pairs.Sum(pair => (pair.Spot - meanSpot) * (pair.Spot - meanSpot));
        if (variance < 1e-6) return null;
        var slope = pairs.Sum(pair => (pair.Spot - meanSpot) * (pair.Retail - meanRetail)) / variance;
        return slope is < MinimumSlope or > MaximumSlope ? null : (meanRetail - slope * meanSpot, slope);
    }
}
