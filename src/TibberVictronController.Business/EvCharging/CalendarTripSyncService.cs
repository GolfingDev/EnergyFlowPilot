using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

public sealed record CalendarTripSyncDependencies(
    ICalendarSource Calendar,
    IRoutePlanner Routes,
    IEvTripRepository Trips,
    IControllerSettingStore Settings,
    IUtcClock Clock,
    IUserCalendarRepository? UserCalendars = null);

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
    // Calendar routes from an older search are searched again once (see EvTripRouteVersion).
    public const int CurrentRouteVersion = EvTripRouteVersion.Current;
    // Closer than this is home or next door, not a trip.
    private const double MinimumDistanceKm = 1;

    private sealed record CalendarSourceInfo(string Mailbox, string? OwnerId, string? OwnerName, string KeyPrefix);

    public async Task<CalendarSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var now = dependencies.Clock.UtcNow;
        var sources = await SourcesAsync(cancellationToken);
        if (sources.Count == 0)
            throw new InvalidOperationException("Kein Kalender verbunden. Auf der Seite Auto \u201eMeinen Kalender verbinden\u201c w\u00e4hlen.");
        var existing = (await dependencies.Trips.GetCalendarTripsAsync(cancellationToken))
            .Where(trip => trip.ExternalId is not null)
            .ToDictionary(trip => trip.ExternalId!, StringComparer.Ordinal);
        var home = await ReadHomeAsync(cancellationToken);
        var routes = new Dictionary<string, RouteEstimate?>(StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<CalendarUnresolved>();
        var errors = new List<string>();
        var synced = new HashSet<string>(StringComparer.Ordinal);
        // The same appointment can be in several calendars (an invitation to two family members); one car makes it
        // one trip. The first calendar wins.
        var seen = new HashSet<(string Subject, DateTimeOffset Start, DateTimeOffset End)>();
        var eventsRead = 0;

        foreach (var source in sources)
        {
            IReadOnlyList<CalendarEvent> events;
            try
            {
                events = await dependencies.Calendar.GetEventsAsync(source.Mailbox, now.AddDays(-1), now + EvTripPlanningService.SeriesHorizon, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
            {
                // One unreadable calendar must not remove the trips of the others (or its own).
                errors.Add($"{source.OwnerName ?? source.Mailbox}: {exception.Message}");
                continue;
            }

            synced.Add(source.KeyPrefix);
            eventsRead += events.Count;
            foreach (var appointment in events.Where(appointment => !appointment.IsCancelled))
            {
                if (!seen.Add((appointment.Subject.Trim().ToLowerInvariant(), appointment.StartUtc, appointment.EndUtc))) continue;
                var key = source.KeyPrefix + appointment.Id;
                if (await SyncAppointmentAsync(appointment, key, source, existing, home, routes, unresolved, now, cancellationToken))
                    kept.Add(key);
            }
        }

        // Remove trips of calendars that were read (appointment gone) or that are no longer connected at all. The
        // latter happens even if no calendar could be read this time: a disconnected calendar's trips must not linger.
        var prefixes = sources.Select(source => source.KeyPrefix).ToArray();
        var removed = existing.Values
            .Where(trip => !kept.Contains(trip.ExternalId!))
            .Where(trip => PrefixOf(trip.ExternalId!, prefixes) is not { } prefix || synced.Contains(prefix))
            .Select(trip => trip.Id).ToArray();
        if (removed.Length > 0) await dependencies.Trips.DeleteAsync(removed, cancellationToken);
        if (synced.Count == 0) throw new InvalidOperationException(string.Join(" ", errors));
        return new CalendarSyncResult(now, eventsRead, kept.Count, unresolved) { SourceErrors = errors };
    }

    private async Task<bool> SyncAppointmentAsync(CalendarEvent appointment, string key, CalendarSourceInfo source,
        Dictionary<string, EvTrip> existing, GeoPoint home, Dictionary<string, RouteEstimate?> routes, List<CalendarUnresolved> unresolved,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (start, end) = OnSite(appointment);
        existing.TryGetValue(key, out var known);
        if (known is { HasLocationOverride: true })
        {
            // The operator chose the place: keep it and its route, follow only the appointment times.
            var keptDrive = TimeSpan.FromMinutes(known.DurationMinutes);
            if (end + keptDrive <= now) return false;
            await dependencies.Trips.UpsertCalendarTripAsync(new CalendarTripUpdate(key, Truncate(appointment.Subject, 200),
                known.Destination, start - keptDrive, end + keptDrive, null, known.RouteVersion)
            { OwnerId = source.OwnerId, OwnerName = source.OwnerName }, now, cancellationToken);
            return true;
        }

        if (CalendarLocationExtractor.Extract(appointment) is not { } extracted) return false;
        if (await RefineAsync(extracted, home, cancellationToken) is not { } candidate) return false;
        RouteEstimate? route = null;
        double durationMinutes;
        if (known is not null && known.RouteVersion >= CurrentRouteVersion &&
            string.Equals(known.Destination, candidate.Query, StringComparison.OrdinalIgnoreCase))
        {
            durationMinutes = known.DurationMinutes;
            if (known.DistanceKm < MinimumDistanceKm) return false;
        }
        else
        {
            route = await RouteAsync(home, candidate, routes, cancellationToken);
            if (route is null)
            {
                unresolved.Add(new CalendarUnresolved(key, appointment.Subject, candidate.Query, start, end)
                    { OwnerId = source.OwnerId, OwnerName = source.OwnerName });
                return false;
            }

            if (route.DistanceKm < MinimumDistanceKm) return false;
            durationMinutes = route.DurationMinutes;
        }

        var drive = TimeSpan.FromMinutes(durationMinutes);
        if (end + drive <= now) return false;
        await dependencies.Trips.UpsertCalendarTripAsync(new CalendarTripUpdate(key, Truncate(appointment.Subject, 200),
            Truncate(candidate.Query, 200), start - drive, end + drive, route, CurrentRouteVersion)
        { OwnerId = source.OwnerId, OwnerName = source.OwnerName }, now, cancellationToken);
        return true;
    }

    /// <summary>
    /// The calendars to read: every user's own calendar connected on the car page, plus the older mailbox setting
    /// (owner-less, kept so an existing setup keeps working).
    /// </summary>
    private async Task<IReadOnlyList<CalendarSourceInfo>> SourcesAsync(CancellationToken cancellationToken)
    {
        var sources = new List<CalendarSourceInfo>();
        var legacy = (await dependencies.Settings.GetSettingAsync(ControllerSettingDefaults.EvCalendarMailboxKey, cancellationToken))?.Value?.Trim();
        var hasLegacy = !string.IsNullOrWhiteSpace(legacy) && legacy != ControllerSettingDefaults.EvCalendarMailboxNotConfigured;
        var users = dependencies.UserCalendars is null ? [] : await dependencies.UserCalendars.GetAllAsync(cancellationToken);
        foreach (var calendar in users)
        {
            // The user connected the mailbox that is also the shared setting: read it once, as the user's calendar, but
            // keep the older keys so places chosen for those appointments stay.
            var sameAsLegacy = hasLegacy && string.Equals(calendar.Mailbox.Trim(), legacy, StringComparison.OrdinalIgnoreCase);
            sources.Add(new CalendarSourceInfo(calendar.Mailbox, calendar.UserId, calendar.Name, sameAsLegacy ? string.Empty : calendar.UserId + "|"));
        }

        if (hasLegacy && sources.All(source => source.KeyPrefix.Length > 0))
            sources.Add(new CalendarSourceInfo(legacy!, null, null, string.Empty));
        return sources;
    }

    // Keys of user calendars carry "<user id>|"; keys without a known prefix belong to the older mailbox setting ("")
    // or to a calendar that was disconnected (null: remove).
    private static string? PrefixOf(string key, IReadOnlyList<string> prefixes)
    {
        var separator = key.IndexOf('|');
        var prefix = separator < 0 ? string.Empty : key[..(separator + 1)];
        return prefixes.Contains(prefix) ? prefix : null;
    }

    /// <summary>
    /// Turns subject hints into the place to search: the nearest town named in the subject refines a general location
    /// ("Lidl" + "Tennis Neuhof" = "Lidl, Neuhof") or is the place itself. Null when nothing is left.
    /// </summary>
    private async Task<CalendarLocationCandidate?> RefineAsync(CalendarLocationCandidate candidate, GeoPoint home, CancellationToken cancellationToken)
    {
        if (candidate.SubjectHints.Count == 0) return candidate;
        string? town = null;
        var nearest = double.MaxValue;
        foreach (var word in candidate.SubjectHints)
        {
            if (await SettlementAsync(word, home, cancellationToken) is not { } point) continue;
            var distance = Math.Abs(point.Latitude - home.Latitude) + Math.Abs(point.Longitude - home.Longitude);
            if (distance < nearest) (town, nearest) = (word, distance);
        }

        if (candidate.Query.Length == 0) return town is null ? null : new CalendarLocationCandidate(town, true);
        return town is null ? candidate : new CalendarLocationCandidate($"{candidate.Query}, {town}", false);
    }

    // Subject words are checked once a day: most are no town ("Tennis", "Training"), and every sync would ask again.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, GeoPoint? Point)> SettlementCache =
        new(StringComparer.OrdinalIgnoreCase);
    private const double SettlementRadiusKm = 40;

    private async Task<GeoPoint?> SettlementAsync(string word, GeoPoint home, CancellationToken cancellationToken)
    {
        var now = dependencies.Clock.UtcNow;
        var key = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{word}|{home.Latitude:F3}|{home.Longitude:F3}");
        if (SettlementCache.TryGetValue(key, out var cached) && now - cached.At < TimeSpan.FromDays(1)) return cached.Point;
        GeoPoint? point;
        try
        {
            point = await dependencies.Routes.FindSettlementNearAsync(word, home, SettlementRadiusKm, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        SettlementCache[key] = (now, point);
        return point;
    }

    /// <summary>
    /// Makes an appointment whose place was not found a trip at the place the operator chose; later syncs keep it.
    /// </summary>
    public async Task SetPlaceAsync(CalendarUnresolved appointment, string label, GeoPoint? target, CancellationToken cancellationToken)
    {
        var home = await ReadHomeAsync(cancellationToken);
        var route = target is null
            ? await dependencies.Routes.EstimateAsync(home, label, false, cancellationToken)
            : await dependencies.Routes.RouteToAsync(home, target, label, cancellationToken);
        var drive = TimeSpan.FromMinutes(route.DurationMinutes);
        await dependencies.Trips.UpsertCalendarTripAsync(new CalendarTripUpdate(appointment.EventId, Truncate(appointment.Subject, 200),
            Truncate(label, 200), appointment.StartUtc - drive, appointment.EndUtc + drive, route, CurrentRouteVersion)
            { IsLocationOverride = true, OwnerId = appointment.OwnerId, OwnerName = appointment.OwnerName },
            dependencies.Clock.UtcNow, cancellationToken);
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
