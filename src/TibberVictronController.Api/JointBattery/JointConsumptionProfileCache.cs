using TibberVictronController.Business.Decisions;

namespace TibberVictronController.Api.JointBattery;

/// <summary>
/// Keeps the learned joint consumption model between planner runs. Learning reads 14 days of raw samples (about one
/// per second) and took 25-30 s on the Raspberry Pi when it ran on every plan (about every 30 s), close to the 75 s
/// plan freshness limit. Like the single-battery path, the model is rebuilt at most once per hour, and earlier when the
/// local day, the time zone or the temperature input changes, or when the plan needs temperatures beyond the ones it
/// was learned with (new day-ahead prices extend the horizon). The current quarter hour does not depend on it: the
/// planner replaces its consumption with the live net load.
/// </summary>
public sealed class JointConsumptionProfileCache
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(1);

    private sealed record Entry(ConsumptionForecastModel Model, DateTimeOffset BuiltAtUtc, DateOnly LocalDate, string ZoneId,
        bool WithTemperatures, DateTimeOffset TemperaturesUntilUtc);

    private Entry? current;

    /// <param name="temperaturesUntilUtc">End of the temperature range the plan needs (forecast horizon plus one day).</param>
    public ConsumptionForecastModel? TryGet(DateTimeOffset nowUtc, TimeZoneInfo zone, bool withTemperatures, DateTimeOffset temperaturesUntilUtc)
    {
        var entry = Volatile.Read(ref current);
        if (entry is null || nowUtc < entry.BuiltAtUtc || nowUtc - entry.BuiltAtUtc >= MaximumAge) return null;
        if (entry.ZoneId != zone.Id || entry.LocalDate != LocalDate(nowUtc, zone) || entry.WithTemperatures != withTemperatures) return null;
        if (withTemperatures && temperaturesUntilUtc > entry.TemperaturesUntilUtc) return null;
        return entry.Model;
    }

    public void Store(ConsumptionForecastModel model, DateTimeOffset builtAtUtc, TimeZoneInfo zone, bool withTemperatures,
        DateTimeOffset temperaturesUntilUtc) =>
        Volatile.Write(ref current, new(model, builtAtUtc, LocalDate(builtAtUtc, zone), zone.Id, withTemperatures, temperaturesUntilUtc));

    private static DateOnly LocalDate(DateTimeOffset utc, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);
}
