using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

public sealed record CalendarTripSyncDependencies(
    ICalendarSource Calendar,
    IRoutePlanner Routes,
    IEvTripRepository Trips,
    IControllerSettingStore Settings,
    IUtcClock Clock);

/// <summary>
/// Turns calendar appointments with a place into trips: departure = start minus driving time, return = end plus
/// driving time. Appointments that disappear or lose their place remove their trip; an ignored trip stays ignored.
/// The route is only requested again when the place changes.
/// </summary>
public sealed class CalendarTripSyncService(CalendarTripSyncDependencies dependencies)
{
    private static readonly TimeZoneInfo LocalZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    // All-day appointments (e.g. a visit or a fair) have no times: assumed on site from 08:00 to 18:00 local time.
    private static readonly TimeSpan AllDayStartLocal = TimeSpan.FromHours(8);
    private static readonly TimeSpan AllDayEndLocal = TimeSpan.FromHours(18);
    // 1: places are searched around home first (2026-11-01). Routes from an older search are searched again once.
    public const int CurrentRouteVersion = 1;
    // Closer than this is home or next door, not a trip.
    private const double MinimumDistanceKm = 1;

    public async Task<CalendarSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var now = dependencies.Clock.UtcNow;
        var events = await dependencies.Calendar.GetEventsAsync(now.AddDays(-1), now + EvTripPlanningService.SeriesHorizon, cancellationToken);
        var existing = (await dependencies.Trips.GetCalendarTripsAsync(cancellationToken))
            .Where(trip => trip.ExternalId is not null)
            .ToDictionary(trip => trip.ExternalId!, StringComparer.Ordinal);
        var home = await ReadHomeAsync(cancellationToken);
        var routes = new Dictionary<string, RouteEstimate?>(StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (var appointment in events.Where(appointment => !appointment.IsCancelled))
        {
            if (CalendarLocationExtractor.Extract(appointment) is not { } candidate) continue;
            var (start, end) = OnSite(appointment);
            existing.TryGetValue(appointment.Id, out var known);
            RouteEstimate? route = null;
            double durationMinutes;
            if (known is not null && known.RouteVersion >= CurrentRouteVersion &&
                string.Equals(known.Destination, candidate.Query, StringComparison.OrdinalIgnoreCase))
            {
                durationMinutes = known.DurationMinutes;
                if (known.DistanceKm < MinimumDistanceKm) continue;
            }
            else
            {
                route = await RouteAsync(home, candidate, routes, cancellationToken);
                if (route is null)
                {
                    unresolved.Add($"{appointment.Subject} ({Local(appointment.StartUtc)}): Ort „{candidate.Query}“ nicht gefunden");
                    continue;
                }

                if (route.DistanceKm < MinimumDistanceKm) continue;
                durationMinutes = route.DurationMinutes;
            }

            var drive = TimeSpan.FromMinutes(durationMinutes);
            if (end + drive <= now) continue;
            await dependencies.Trips.UpsertCalendarTripAsync(new CalendarTripUpdate(appointment.Id, Truncate(appointment.Subject, 200),
                Truncate(candidate.Query, 200), start - drive, end + drive, route, CurrentRouteVersion), now, cancellationToken);
            kept.Add(appointment.Id);
        }

        var removed = existing.Values.Where(trip => !kept.Contains(trip.ExternalId!)).Select(trip => trip.Id).ToArray();
        if (removed.Length > 0) await dependencies.Trips.DeleteAsync(removed, cancellationToken);
        return new CalendarSyncResult(now, events.Count, kept.Count, unresolved);
    }

    private async Task<RouteEstimate?> RouteAsync(GeoPoint home, CalendarLocationCandidate candidate,
        Dictionary<string, RouteEstimate?> cache, CancellationToken cancellationToken)
    {
        // One lookup per place and sync; OpenStreetMap asks for at most one request per second.
        if (cache.TryGetValue(candidate.Query, out var cached)) return cached;
        if (cache.Count > 0) await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        RouteEstimate? route;
        try
        {
            route = await dependencies.Routes.EstimateAsync(home, candidate.Query, candidate.FromSubject, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            route = null;
        }

        cache[candidate.Query] = route;
        return route;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) OnSite(CalendarEvent appointment)
    {
        if (!appointment.IsAllDay) return (appointment.StartUtc, appointment.EndUtc);
        // All-day dates are calendar days; the end is exclusive (midnight after the last day).
        var firstDay = appointment.StartUtc.UtcDateTime.Date;
        var lastDay = appointment.EndUtc.UtcDateTime.Date.AddDays(-1);
        if (lastDay < firstDay) lastDay = firstDay;
        return (AtLocal(firstDay + AllDayStartLocal), AtLocal(lastDay + AllDayEndLocal));
    }

    private static DateTimeOffset AtLocal(DateTime local) =>
        new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), LocalZone.GetUtcOffset(local)).ToUniversalTime();

    private static string Local(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, LocalZone).ToString("ddd dd.MM. HH:mm", CultureInfo.GetCultureInfo("de-DE"));

    private static string Truncate(string value, int maximumLength) => value.Length <= maximumLength ? value : value[..maximumLength];

    private async Task<GeoPoint> ReadHomeAsync(CancellationToken cancellationToken)
    {
        async Task<double> Read(string key) =>
            double.TryParse((await dependencies.Settings.GetSettingAsync(key, cancellationToken))?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new InvalidOperationException($"Die Einstellung {key} fehlt oder ist keine Zahl.");
        return new GeoPoint(await Read(ControllerSettingDefaults.PvForecastLatitudeKey), await Read(ControllerSettingDefaults.PvForecastLongitudeKey));
    }
}
