using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Services;

/// <summary>Compares observed counter differences, without inventing consumption during outages.</summary>
public sealed class WeishauptEnergyHistoryService(IWeishauptBiblockTelemetryRepository telemetry, IUtcClock clock)
{
    private static readonly TimeZoneInfo DeviceTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public async Task<WeishauptEnergyHistory> GetAsync(int days, DateTimeOffset? endsAtUtc, CancellationToken ct)
    {
        if (days is not (1 or 7 or 30))
            throw new ArgumentException("Der Zeitraum muss 1, 7 oder 30 Tage betragen.");
        var requestedEnd = (endsAtUtc ?? clock.UtcNow).ToUniversalTime();
        if (requestedEnd > clock.UtcNow || requestedEnd < clock.UtcNow.AddDays(-365))
            throw new ArgumentException("Das Zeitraumende muss innerhalb des letzten Jahres liegen und darf nicht in der Zukunft liegen.");
        // Compare equal elapsed UTC hours, including across daylight-saving transitions.
        var end = Hour(requestedEnd);
        var start = end.AddDays(-days);
        var previousStart = start.AddDays(-days);
        var samples = await telemetry.GetSamplesAsync(previousStart, end.AddHours(1), ct);
        // First observation per hour approximates its boundary; retain actual times to expose imprecision.
        var readings = samples.Where(s => s.MeasuredAtUtc <= requestedEnd)
            .OrderBy(s => s.MeasuredAtUtc).GroupBy(s => Hour(s.MeasuredAtUtc))
            .ToDictionary(g => g.Key, g => g.First());
        return new(start, end, Build(start, end, readings), Build(previousStart, start, readings));
    }

    private static IReadOnlyList<WeishauptEnergyHour> Build(DateTimeOffset start, DateTimeOffset end,
        IReadOnlyDictionary<DateTimeOffset, WeishauptBiblockTelemetrySample> readings)
    {
        var result = new List<WeishauptEnergyHour>();
        for (var hour = start; hour < end; hour = hour.AddHours(1))
        {
            readings.TryGetValue(hour, out var before);
            readings.TryGetValue(hour.AddHours(1), out var after);
            decimal? consumption = null;
            // Never spread a multi-hour outage across apparently measured hourly values.
            if (before is not null && after is not null &&
                after.MeasuredAtUtc - before.MeasuredAtUtc >= TimeSpan.FromMinutes(30) &&
                after.MeasuredAtUtc - before.MeasuredAtUtc <= TimeSpan.FromMinutes(90))
                consumption = Difference(before, after);
            result.Add(new(hour, before?.MeasuredAtUtc, after?.MeasuredAtUtc, consumption,
                before is null || after is null ? null : (before.OutdoorTemperatureCelsius + after.OutdoorTemperatureCelsius) / 2));
        }
        return result;
    }

    private static decimal? Difference(WeishauptBiblockTelemetrySample before, WeishauptBiblockTelemetrySample after)
    {
        var localBefore = TimeZoneInfo.ConvertTime(before.MeasuredAtUtc, DeviceTimeZone);
        var localAfter = TimeZoneInfo.ConvertTime(after.MeasuredAtUtc, DeviceTimeZone);
        // Annual counter avoids midnight/monthly resets. A falling counter is not negative consumption.
        if (localBefore.Year == localAfter.Year && before.ElectricalYearKwh is { } a && after.ElectricalYearKwh is { } b)
            return b >= a ? b - a : null;
        if (localBefore.Date == localAfter.Date && before.ElectricalTodayKwh is { } todayBefore && after.ElectricalTodayKwh is { } todayAfter)
            return todayAfter >= todayBefore ? todayAfter - todayBefore : null;
        // Also works at the year boundary if the device supplies yesterday's final daily counter.
        if (localAfter.Date == localBefore.Date.AddDays(1) && before.ElectricalTodayKwh is { } oldDay &&
            after.ElectricalYesterdayKwh is { } yesterday && after.ElectricalTodayKwh is { } newDay && yesterday >= oldDay)
            return yesterday - oldDay + newDay;
        return null;
    }

    private static DateTimeOffset Hour(DateTimeOffset value) => new(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero);
}

public sealed record WeishauptEnergyHistory(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc,
    IReadOnlyList<WeishauptEnergyHour> Hours, IReadOnlyList<WeishauptEnergyHour> PreviousHours);
public sealed record WeishauptEnergyHour(DateTimeOffset StartsAtUtc, DateTimeOffset? MeasuredFromUtc,
    DateTimeOffset? MeasuredToUtc, decimal? ElectricalKwh, decimal? OutdoorTemperatureCelsius);
