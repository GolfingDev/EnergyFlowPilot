using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Business.EvCharging;

/// <summary>Car slots with forecast prices appended, and from when the prices are only forecast.</summary>
public sealed record EvForecastExtension(IReadOnlyList<EvChargeSlotInput> Slots, DateTimeOffset? ForecastFromUtc);

/// <summary>
/// Appends exchange price forecasts after the published prices, so the car plan can judge whether waiting for the
/// night pays off before its prices are out (operator 2026-11-11: waiting may already have missed the cheapest hours).
/// The conversion to tariff prices is <see cref="RetailPriceForecast"/>. Forecast slots are marked estimated, so the
/// planner adds its safety hurdle; they carry no PV, the PV forecast reaches only as far as the battery plan.
/// </summary>
public static class EvPriceForecastExtension
{
    public static EvForecastExtension Extend(IReadOnlyList<EvChargeSlotInput> slots, IReadOnlyList<PriceForecastSnapshot> snapshots, DateTimeOffset horizonEndUtc)
    {
        if (slots.Count == 0 || snapshots.Count == 0) return new(slots, null);
        var end = slots.Max(slot => slot.EndsAtUtc);
        if (end >= horizonEndUtc) return new(slots, null);
        var published = slots.Where(slot => !slot.IsPriceEstimated).Select(slot => new RetailPricePoint(slot.StartsAtUtc, slot.PricePerKwh));
        if (RetailPriceForecast.Fit(published, snapshots) is not { } line) return new(slots, null);

        var added = RetailPriceForecast.Continue(end, horizonEndUtc, snapshots, line)
            .Select(point => new EvChargeSlotInput(point.StartsAtUtc, point.StartsAtUtc.AddMinutes(15), point.PricePerKwh, true, 0))
            .ToArray();
        return added.Length == 0 ? new(slots, null) : new([.. slots, .. added], end);
    }
}
