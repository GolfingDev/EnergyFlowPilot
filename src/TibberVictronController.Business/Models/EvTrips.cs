namespace TibberVictronController.Business.Models;

/// <summary>
/// A planned round trip. Distance and duration are one way, as calculated from the route. A series repeats on the
/// weekdays in <see cref="RecurrenceDays"/> (bit 1 &lt;&lt; DayOfWeek) at the local departure time of the first trip.
/// </summary>
public sealed record EvTrip(
    int Id,
    DateTimeOffset DepartureAtUtc,
    DateTimeOffset? ReturnAtUtc,
    string Destination,
    string ResolvedAddress,
    double DistanceKm,
    double DurationMinutes,
    DateTimeOffset CreatedAtUtc,
    int RecurrenceDays = 0,
    DateTimeOffset? RecurrenceUntilUtc = null,
    string Source = EvTripSources.Manual,
    string? ExternalId = null,
    string? Subject = null,
    bool IsIgnored = false,
    IReadOnlyList<DateOnly>? SkippedDates = null,
    DateOnly? OccurrenceDate = null,
    GeoPoint? Target = null,
    int RouteVersion = 0,
    bool HasLocationOverride = false,
    string? OwnerId = null,
    string? OwnerName = null)
{
    public bool IsRecurring => RecurrenceDays != 0;

    public bool IsFromCalendar => Source == EvTripSources.Calendar;

    public static int MaskOf(IEnumerable<DayOfWeek> days) => days.Aggregate(0, (mask, day) => mask | (1 << (int)day));

    public static IReadOnlyList<DayOfWeek> DaysOf(int mask) =>
        Enum.GetValues<DayOfWeek>().Where(day => (mask & (1 << (int)day)) != 0).ToArray();
}

public sealed record EvTripDraft(
    DateTimeOffset DepartureAtUtc,
    DateTimeOffset? ReturnAtUtc,
    string Destination,
    string ResolvedAddress,
    double Latitude,
    double Longitude,
    double DistanceKm,
    double DurationMinutes,
    int RecurrenceDays = 0,
    DateTimeOffset? RecurrenceUntilUtc = null,
    string? OwnerId = null,
    string? OwnerName = null);

/// <summary>
/// Who acts on trips. Drivers change only their own trips; admins all. Trips without owner (before per-user trips
/// existed) belong to the admins.
/// </summary>
public sealed record EvTripActor(string UserId, string Name, bool IsAdmin)
{
    public bool MayChange(EvTrip trip) => IsAdmin || trip.OwnerId is not null && trip.OwnerId == UserId;
}

/// <summary>A user's calendar connected on the car page; the mailbox is the user's own sign-in address.</summary>
public sealed record UserCalendar(string UserId, string Name, string Mailbox, DateTimeOffset ConnectedAtUtc);

public static class EvTripSources
{
    public const string Manual = "Manual";
    public const string Calendar = "Calendar";
}

/// <summary>
/// A calendar appointment turned into a trip. Route is null when the location did not change, so the stored route
/// is kept and OpenStreetMap is not asked again.
/// </summary>
public sealed record CalendarTripUpdate(
    string ExternalId,
    string Subject,
    string Destination,
    DateTimeOffset DepartureAtUtc,
    DateTimeOffset ReturnAtUtc,
    RouteEstimate? Route,
    int RouteVersion = 0)
{
    /// <summary>The place was chosen by the operator; later syncs keep it and only follow the appointment times.</summary>
    public bool IsLocationOverride { get; init; }

    public string? OwnerId { get; init; }

    public string? OwnerName { get; init; }
}

/// <summary>An appointment whose place was not found; the operator can choose the place on the car page.</summary>
public sealed record CalendarUnresolved(string EventId, string Subject, string Query, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public string? OwnerId { get; init; }

    public string? OwnerName { get; init; }

    public string Text(TimeZoneInfo zone) =>
        $"{Subject} ({TimeZoneInfo.ConvertTime(StartUtc, zone).ToString("ddd dd.MM. HH:mm", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))}): Ort \u201e{Query}\u201c nicht gefunden";
}

/// <summary>One appointment as read from the calendar; times in UTC.</summary>
/// <summary>
/// Version of the route logic a trip's distance comes from. Trips with an older version are routed again once.
/// 1: places searched around home first; 2: shortest of the nearly equally fast routes (both 2026-11-01).
/// </summary>
public static class EvTripRouteVersion
{
    public const int Current = 2;
}

public sealed record CalendarEvent(
    string Id,
    string Subject,
    string? Location,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool IsAllDay,
    bool IsOnlineMeeting,
    bool IsCancelled);

public sealed record CalendarSyncResult(
    DateTimeOffset SyncedAtUtc,
    int EventsRead,
    int TripsFound,
    IReadOnlyList<CalendarUnresolved> Unresolved)
{
    /// <summary>Calendars that could not be read this time; their trips stay as they were.</summary>
    public IReadOnlyList<string> SourceErrors { get; init; } = [];
}

public sealed record GeoPoint(double Latitude, double Longitude);

/// <summary>A suggestion while typing an address: readable label and coordinates.</summary>
public sealed record PlaceSuggestion(string Label, double Latitude, double Longitude);

/// <summary>One-way route from home to a destination.</summary>
public sealed record RouteEstimate(string ResolvedAddress, GeoPoint Destination, double DistanceKm, double DurationMinutes);

/// <summary>Car and wallbox values the state-of-charge plan is based on.</summary>
public sealed record EvCarParameters(
    double UsableCapacityKwh,
    double ConsumptionKwhPer100Km,
    double MinimumSocPercent,
    double MarginPercent,
    double ChargePowerWatts)
{
    /// <summary>The wallbox maximum from the settings; <see cref="ChargePowerWatts"/> is lower when the car limits.</summary>
    public double? WallboxMaximumPowerWatts { get; init; }

    /// <summary>AC current per phase the car reports as its limit, or null when it reports none.</summary>
    public double? CarAcLimitAmps { get; init; }
}

/// <summary>Requirement for one trip: the state of charge the car needs when it leaves.</summary>
public sealed record EvTripRequirement(
    EvTrip Trip,
    DateTimeOffset ReturnAtUtc,
    bool ReturnIsAssumed,
    double EnergyKwh,
    double RequiredSocPercent,
    double ExpectedSocAtReturnPercent,
    DateTimeOffset? LatestChargeStartUtc,
    bool IsReachable,
    string? Note,
    double WinterSurchargePercent = 0,
    double? TemperatureCelsius = null);

public sealed record EvSocPoint(DateTimeOffset AtUtc, double SocPercent);

/// <summary>
/// The minimal state-of-charge course: charged only as much as needed and as late as possible at full wallbox power.
/// Price-optimised charging (stage 4) may charge earlier, but never ends below this course.
/// </summary>
public sealed record EvTripPlan(
    DateTimeOffset CalculatedAtUtc,
    double? CurrentSocPercent,
    bool CurrentSocIsAssumed,
    IReadOnlyList<EvTripRequirement> Trips,
    IReadOnlyList<EvSocPoint> Course,
    IReadOnlyList<string> Warnings);
