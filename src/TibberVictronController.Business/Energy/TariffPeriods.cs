using System.Text.Json;
using System.Text.Json.Serialization;

namespace TibberVictronController.Business.Energy;

/// <summary>
/// Contract details valid from a local day until the next period starts: supplier, monthly base fee (everything billed
/// per month: supplier, grid and metering) and feed-in compensation. Null means not known, never zero.
/// </summary>
public sealed record TariffPeriod(DateOnly ValidFrom, string Supplier, decimal? MonthlyBaseFee, decimal? FeedInPricePerKwh);

public static class TariffPeriods
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Stored periods; an unreadable value counts as no periods (the page then says so) instead of failing.</summary>
    public static IReadOnlyList<TariffPeriod> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<TariffPeriod[]>(json, Json) ?? []).OrderBy(period => period.ValidFrom).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IReadOnlyList<TariffPeriod> periods) => JsonSerializer.Serialize(periods, Json);

    public static IReadOnlyList<TariffPeriod> Validate(IEnumerable<TariffPeriod> periods)
    {
        var ordered = periods.Select(period => period with { Supplier = period.Supplier?.Trim() ?? string.Empty }).OrderBy(period => period.ValidFrom).ToArray();
        if (ordered.Length > 50) throw new ArgumentException("Höchstens 50 Tarifzeiträume sind möglich.");
        foreach (var period in ordered)
        {
            if (period.Supplier.Length is 0 or > 80) throw new ArgumentException("Jeder Tarifzeitraum braucht einen Anbieternamen (höchstens 80 Zeichen).");
            if (period.MonthlyBaseFee is < 0 or > 1000) throw new ArgumentException("Die Grundgebühr muss zwischen 0 und 1000 EUR im Monat liegen.");
            if (period.FeedInPricePerKwh is < -1 or > 1) throw new ArgumentException("Die Einspeisevergütung muss zwischen -1 und 1 EUR/kWh liegen.");
        }

        if (ordered.Select(period => period.ValidFrom).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Zwei Tarifzeiträume beginnen am selben Tag.");
        return ordered;
    }

    public static TariffPeriod? ValidOn(IReadOnlyList<TariffPeriod> periods, DateOnly day) => periods.LastOrDefault(period => period.ValidFrom <= day);

    /// <summary>Base fee of one local day: the monthly fee spread evenly over the days of that month.</summary>
    public static decimal? DailyBaseFee(TariffPeriod? period, DateOnly day) =>
        period?.MonthlyBaseFee is { } fee ? fee / DateTime.DaysInMonth(day.Year, day.Month) : null;
}
