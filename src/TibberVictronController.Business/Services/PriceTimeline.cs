using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Services;

public enum PriceTimelineKind
{
    /// <summary>Already over: the price that applied.</summary>
    Past,
    /// <summary>Published by the supplier.</summary>
    Published,
    /// <summary>Estimated by the supplier's price source (not published yet).</summary>
    Estimated,
    /// <summary>Exchange price forecast (energyforecast.de) converted to the tariff.</summary>
    Forecast
}

public sealed record PriceTimelineSlot(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, double PricePerKwh, PriceTimelineKind Kind);

/// <param name="Past">Prices that applied before now (decision log or the price source).</param>
/// <param name="Upcoming">The supplier's prices from now on; they win over <paramref name="Past"/> where both overlap.</param>
/// <param name="Snapshots">Stored exchange prices and forecasts (see <see cref="RetailPriceForecast.Fresh"/>).</param>
public sealed record PriceTimelineInput(
    DateTimeOffset NowUtc,
    DateTimeOffset FromUtc,
    DateTimeOffset UntilUtc,
    IReadOnlyList<TibberPriceForecastSlot> Past,
    IReadOnlyList<TibberPriceForecastSlot> Upcoming,
    IReadOnlyList<PriceForecastSnapshot> Snapshots);

public sealed record PriceTimelineResult(IReadOnlyList<PriceTimelineSlot> Slots, DateTimeOffset? ForecastFromUtc);

/// <summary>
/// Price course for the dashboard (operator 2026-11-11): the last hours, the published prices and, after them, the
/// forecast converted to the tariff, each marked by its kind so a forecast never looks like a published price.
/// </summary>
public static class PriceTimeline
{
    public static PriceTimelineResult Build(PriceTimelineInput input)
    {
        var supplier = input.Upcoming.Where(slot => slot.TimeSlot.EndsAtUtc > input.FromUtc && slot.TimeSlot.StartsAtUtc < input.UntilUtc).ToArray();
        // History may come in another slot length than the supplier's prices; where they overlap, the supplier counts.
        var history = input.Past.Where(slot => slot.TimeSlot.EndsAtUtc > input.FromUtc && slot.TimeSlot.StartsAtUtc < input.NowUtc &&
            !supplier.Any(other => other.TimeSlot.StartsAtUtc < slot.TimeSlot.EndsAtUtc && slot.TimeSlot.StartsAtUtc < other.TimeSlot.EndsAtUtc));
        var known = supplier.Concat(history).OrderBy(slot => slot.TimeSlot.StartsAtUtc)
            .Select(slot => new PriceTimelineSlot(slot.TimeSlot.StartsAtUtc, slot.TimeSlot.EndsAtUtc, (double)slot.TotalPricePerKwh, Kind(slot, input.NowUtc)))
            .ToList();
        if (known.Count == 0) return new PriceTimelineResult([], null);

        var end = known.Max(slot => slot.EndsAtUtc);
        var published = known.Where(slot => slot.Kind is PriceTimelineKind.Past or PriceTimelineKind.Published)
            .Select(slot => new RetailPricePoint(slot.StartsAtUtc, slot.PricePerKwh));
        if (end >= input.UntilUtc || RetailPriceForecast.Fit(published, input.Snapshots) is not { } line) return new PriceTimelineResult(known, null);

        var forecast = RetailPriceForecast.Continue(end, input.UntilUtc, input.Snapshots, line)
            .Select(point => new PriceTimelineSlot(point.StartsAtUtc, point.StartsAtUtc.AddMinutes(15), point.PricePerKwh, PriceTimelineKind.Forecast))
            .ToArray();
        return forecast.Length == 0 ? new PriceTimelineResult(known, null) : new PriceTimelineResult([.. known, .. forecast], end);
    }

    private static PriceTimelineKind Kind(TibberPriceForecastSlot slot, DateTimeOffset now) =>
        slot.TimeSlot.EndsAtUtc <= now ? PriceTimelineKind.Past : slot.IsEstimated ? PriceTimelineKind.Estimated : PriceTimelineKind.Published;
}
