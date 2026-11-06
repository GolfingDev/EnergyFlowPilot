using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

/// <summary>
/// The long-term cheap grid price below which the car fills up beyond its need. Null (with the reason) when the
/// price history is too short: then the car fills up only with PV surplus.
/// </summary>
public sealed record EvCheapPriceThreshold(double? PricePerKwh, double Percentile, int HistoryDays, string? Problem);

public static class EvCheapPrice
{
    public const int HistoryDays = 30;
    public const int MinimumDays = 7;

    /// <summary>
    /// The given percentile of the quarter-hour prices of the history. Operator 2026-11-06: 0.42 EUR/kWh is not cheap
    /// just because it is the cheapest price of the next hours; "cheap" is judged over the last weeks.
    /// </summary>
    public static EvCheapPriceThreshold Threshold(IReadOnlyList<TibberPriceForecastSlot> history, double percentile)
    {
        var days = history.Select(slot => DateOnly.FromDateTime(slot.TimeSlot.StartsAtUtc.UtcDateTime)).Distinct().Count();
        if (days < MinimumDays)
            return new EvCheapPriceThreshold(null, percentile, days,
                $"Erst {days} Tage Preisverlauf; aus dem Netz wird erst ab {MinimumDays} Tagen über den Bedarf hinaus geladen.");
        var sorted = history.Select(slot => (double)slot.TotalPricePerKwh).Order().ToArray();
        var index = (int)Math.Clamp(Math.Ceiling(percentile / 100 * sorted.Length) - 1, 0, sorted.Length - 1);
        return new EvCheapPriceThreshold(sorted[index], percentile, days, null);
    }
}
