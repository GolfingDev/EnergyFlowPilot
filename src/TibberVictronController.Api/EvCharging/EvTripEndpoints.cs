using TibberVictronController.Api.Authentication;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.EvCharging;

public static class EvTripEndpoints
{
    public static IEndpointRouteBuilder MapEvTripEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ev-charging/trips", GetPlanAsync).WithName("GetEvTripPlan").WithTags("EvCharging").RequireDriver();
        endpoints.MapGet("/api/ev-charging/my-calendar", GetMyCalendarAsync).WithName("GetMyCalendar").WithTags("EvCharging").RequireDriver();
        endpoints.MapPut("/api/ev-charging/my-calendar", ConnectMyCalendarAsync).WithName("ConnectMyCalendar").WithTags("EvCharging").RequireDriver();
        endpoints.MapDelete("/api/ev-charging/my-calendar", DisconnectMyCalendarAsync).WithName("DisconnectMyCalendar").WithTags("EvCharging").RequireDriver();
        endpoints.MapPost("/api/ev-charging/trips", AddTripAsync).WithName("AddEvTrip").WithTags("EvCharging");
        endpoints.MapDelete("/api/ev-charging/trips/{id:int}", DeleteTripAsync).WithName("DeleteEvTrip").WithTags("EvCharging");
        endpoints.MapPut("/api/ev-charging/trips/{id:int}/ignored", SetIgnoredAsync).WithName("SetEvTripIgnored").WithTags("EvCharging");
        endpoints.MapPut("/api/ev-charging/trips/{id:int}/location", SetLocationAsync).WithName("SetEvTripLocation").WithTags("EvCharging");
        endpoints.MapPost("/api/ev-charging/calendar/location", SetCalendarPlaceAsync).WithName("SetCalendarPlace").WithTags("EvCharging");
        endpoints.MapPut("/api/ev-charging/trips/{id:int}/occurrences/{date}/skipped", SetOccurrenceSkippedAsync).WithName("SetEvTripOccurrenceSkipped").WithTags("EvCharging");
        endpoints.MapGet("/api/ev-charging/charge-plan", GetChargePlanAsync).WithName("GetEvChargePlan").WithTags("EvCharging");
        endpoints.MapGet("/api/ev-charging/control", (EvChargingControlState state) =>
        {
            var current = state.Current;
            return TypedResults.Ok(new EvChargingControlDto(current.Enabled, current.Mode.ToString(), current.Amps, current.Reason,
                current.UpdatedAtUtc == DateTimeOffset.MinValue ? null : current.UpdatedAtUtc, current.CommandProblem));
        }).WithName("GetEvChargingControl").WithTags("EvCharging");
        endpoints.MapGet("/api/ev-charging/calendar", GetCalendarStatusAsync).WithName("GetCalendarSyncStatus").WithTags("EvCharging").RequireDriver();
        endpoints.MapPost("/api/ev-charging/calendar/sync", SyncCalendarAsync).WithName("SyncCalendar").WithTags("EvCharging").RequireDriver();
        return endpoints;
    }

    private static async Task<IResult> GetPlanAsync(HttpContext http, EvTripPlanningService planning, ControllerAuthenticationOptions authentication,
        CancellationToken cancellationToken)
    {
        try
        {
            // Drivers see their own trips in full; other people's trips only as "Fahrt von ...".
            var view = await planning.GetPlanAsync(cancellationToken);
            return TypedResults.Ok(EvTripPlanDtoMapper.Map(view, AdminOnlyEndpoints.Actor(http)));
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.Problem(exception.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> AddTripAsync(HttpContext http, EvTripRequestDto request, EvTripPlanningService planning, CancellationToken cancellationToken)
    {
        try
        {
            if (request.RecurrenceDays?.Any(day => day is < 0 or > 6) == true)
                return TypedResults.BadRequest(new { message = "Wochentage muessen zwischen 0 (Sonntag) und 6 (Samstag) liegen." });
            var trip = await planning.AddTripAsync(new EvTripRequest(request.DepartureAtUtc, request.ReturnAtUtc, request.Destination ?? string.Empty,
                request.RecurrenceDays?.Select(day => (DayOfWeek)day).ToArray(), request.RecurrenceUntilUtc)
            {
                Target = request.Latitude is { } latitude && request.Longitude is { } longitude ? new GeoPoint(latitude, longitude) : null
            }, AdminOnlyEndpoints.Actor(http), cancellationToken);
            return TypedResults.Ok(new { trip.Id });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return TypedResults.BadRequest(new { message = exception.Message });
        }
        catch (HttpRequestException exception)
        {
            return TypedResults.Problem($"OpenStreetMap ist nicht erreichbar: {exception.Message}", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static Task<IResult> DeleteTripAsync(HttpContext http, int id, EvTripPlanningService planning, CancellationToken cancellationToken) =>
        OwnedAsync(async () => await planning.DeleteTripAsync(id, AdminOnlyEndpoints.Actor(http), cancellationToken), "Die Fahrt gibt es nicht (mehr).");

    private static Task<IResult> SetIgnoredAsync(HttpContext http, int id, EvTripIgnoreRequestDto request, EvTripPlanningService planning,
        CancellationToken cancellationToken) =>
        OwnedAsync(async () => await planning.SetIgnoredAsync(id, request.Ignored, AdminOnlyEndpoints.Actor(http), cancellationToken), "Die Fahrt gibt es nicht (mehr).");

    private static Task<IResult> SetOccurrenceSkippedAsync(HttpContext http, int id, string date, EvTripSkipRequestDto request, EvTripPlanningService planning,
        CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
            return Task.FromResult<IResult>(TypedResults.BadRequest(new { message = "Das Datum muss im Format JJJJ-MM-TT angegeben sein." }));
        return OwnedAsync(async () => await planning.SetOccurrenceSkippedAsync(id, day, request.Skipped, AdminOnlyEndpoints.Actor(http), cancellationToken),
            "Die Serie gibt es nicht (mehr).");
    }

    private static Task<IResult> SetLocationAsync(HttpContext http, int id, PlaceRequestDto request, EvTripPlanningService planning, CancellationToken cancellationToken) =>
        OwnedAsync(async () => await planning.SetLocationAsync(id, request.Label ?? string.Empty, Target(request), AdminOnlyEndpoints.Actor(http), cancellationToken),
            "Die Fahrt gibt es nicht (mehr).");

    // Common answers for changes of a trip: not found, someone else's trip, invalid input.
    private static async Task<IResult> OwnedAsync(Func<Task<bool>> change, string notFound)
    {
        try
        {
            return await change() ? TypedResults.NoContent() : TypedResults.NotFound(new { message = notFound });
        }
        catch (UnauthorizedAccessException exception)
        {
            return TypedResults.Json(new { message = exception.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return TypedResults.BadRequest(new { message = exception.Message });
        }
    }

    private static async Task<IResult> GetMyCalendarAsync(HttpContext http, IUserCalendarRepository calendars, ControllerAuthenticationOptions authentication,
        IControllerSettingStore settings, CancellationToken cancellationToken)
    {
        var actor = AdminOnlyEndpoints.Actor(http);
        var connected = authentication.IsEntra ? await calendars.GetAsync(actor.UserId, cancellationToken) : null;
        var enabled = bool.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.EvCalendarEnabledKey, cancellationToken))?.Value, out var on) && on;
        return TypedResults.Ok(new MyCalendarDto(authentication.IsEntra, enabled, AdminOnlyEndpoints.Mailbox(http), connected is not null, connected?.ConnectedAtUtc));
    }

    // The mailbox is the user's own sign-in address; nobody can connect someone else's calendar.
    private static async Task<IResult> ConnectMyCalendarAsync(HttpContext http, IUserCalendarRepository calendars, ControllerAuthenticationOptions authentication,
        CalendarSyncRunner runner, IUtcClock clock, CancellationToken cancellationToken)
    {
        if (!authentication.IsEntra || AdminOnlyEndpoints.Mailbox(http) is not { } mailbox)
            return TypedResults.BadRequest(new { message = "Der eigene Kalender braucht die Anmeldung mit Microsoft 365." });
        var actor = AdminOnlyEndpoints.Actor(http);
        await calendars.SaveAsync(new UserCalendar(actor.UserId, actor.Name, mailbox, clock.UtcNow), cancellationToken);
        await runner.RunAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> DisconnectMyCalendarAsync(HttpContext http, IUserCalendarRepository calendars, CalendarSyncRunner runner,
        CancellationToken cancellationToken)
    {
        await calendars.DeleteAsync(AdminOnlyEndpoints.Actor(http).UserId, cancellationToken);
        // The next sync removes the trips of the disconnected calendar.
        await runner.RunAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // The appointment comes from the last sync on the server, not from the browser.
    private static async Task<IResult> SetCalendarPlaceAsync(HttpContext http, CalendarPlaceRequestDto request, CalendarSyncState state, CalendarTripSyncService sync,
        CancellationToken cancellationToken)
    {
        var actor = AdminOnlyEndpoints.Actor(http);
        var appointment = state.Current.Result?.Unresolved.FirstOrDefault(entry => entry.EventId == request.EventId);
        if (appointment is null) return TypedResults.NotFound(new { message = "Der Termin ist nicht mehr offen; bitte neu abgleichen." });
        if (!actor.IsAdmin && appointment.OwnerId != actor.UserId)
            return TypedResults.Json(new { message = "Nur der eigene Kalender kann bearbeitet werden." }, statusCode: StatusCodes.Status403Forbidden);
        var label = request.Label?.Trim() ?? string.Empty;
        if (label.Length is < 2 or > 200) return TypedResults.BadRequest(new { message = "Der Ort muss zwischen 2 und 200 Zeichen lang sein." });
        try
        {
            await sync.SetPlaceAsync(appointment, label, Target(request), cancellationToken);
            state.Resolved(appointment.EventId);
            return TypedResults.NoContent();
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.BadRequest(new { message = exception.Message });
        }
    }

    private static GeoPoint? Target(PlaceRequestDto request) =>
        request.Latitude is { } latitude && request.Longitude is { } longitude ? new GeoPoint(latitude, longitude) : null;

    private static async Task<IResult> GetCalendarStatusAsync(HttpContext http, CalendarSyncState state, IControllerSettingStore settings,
        ControllerAuthenticationOptions authentication, CancellationToken cancellationToken)
    {
        var enabled = bool.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.EvCalendarEnabledKey, cancellationToken))?.Value, out var value) && value;
        var (result, error, attemptAt) = state.Current;
        var actor = AdminOnlyEndpoints.Actor(http);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        // Everyone sees only the open appointments of their own calendar (admins also the older shared mailbox).
        return TypedResults.Ok(new CalendarSyncStatusDto(enabled, attemptAt, result?.SyncedAtUtc, error, result?.EventsRead, result?.TripsFound,
            (result?.Unresolved ?? []).Where(entry => entry.OwnerId == actor.UserId || actor.IsAdmin && entry.OwnerId is null)
                .Select(entry => new CalendarUnresolvedDto(entry.EventId, entry.Text(zone), entry.Query)).ToArray())
        {
            SourceErrors = result?.SourceErrors ?? []
        });
    }

    private static async Task<IResult> SyncCalendarAsync(CalendarSyncRunner runner, CalendarSyncState state, CancellationToken cancellationToken)
    {
        if (!await runner.RunAsync(cancellationToken))
            return TypedResults.BadRequest(new { message = "Der Kalender ist in den Einstellungen ausgeschaltet." });
        return state.Current.Error is { } error ? TypedResults.BadRequest(new { message = error }) : TypedResults.NoContent();
    }

    private static async Task<IResult> GetChargePlanAsync(HttpContext http, EvChargePlanService chargePlan, CancellationToken cancellationToken)
    {
        try
        {
            var view = await chargePlan.GetAsync(cancellationToken);
            var plan = view.Plan;
            // Viewers see the charging plan, but not where the trips go.
            var destinations = view.Trips.Plan.Trips.Select(trip => trip.Trip.Destination).Where(name => name.Length > 0).Distinct().ToArray();
            var warnings = AdminOnlyEndpoints.IsAdmin(http)
                ? plan.Warnings
                : plan.Warnings.Select(warning => destinations.Aggregate(warning, (text, name) => text.Replace("nach " + name, "zu einer Fahrt"))).ToArray();
            return TypedResults.Ok(new EvChargePlanResponseDto(plan.CalculatedAtUtc, view.SlotSource, Math.Round(plan.GridKwh, 1), Math.Round(plan.PvKwh, 1),
                Math.Round(plan.GridCost, 2), warnings,
                plan.Scenarios.Select(scenario => new EvChargeScenarioDto(scenario.Name, scenario.PvFactor, Math.Round(scenario.GridKwh, 1), Math.Round(scenario.GridCost, 2))).ToArray(),
                plan.Slots.Select(slot => new EvChargeSlotDto(slot.StartsAtUtc, slot.EndsAtUtc, Math.Round(slot.PowerWatts), Math.Round(slot.GridKwh, 2),
                    Math.Round(slot.PvKwh, 2), slot.PricePerKwh, slot.IsPriceEstimated, slot.Reason.ToString())).ToArray()));
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.Problem(exception.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

}

public sealed record MyCalendarDto(bool Available, bool SyncEnabled, string? Mailbox, bool Connected, DateTimeOffset? ConnectedAtUtc);

public sealed record EvTripIgnoreRequestDto(bool Ignored);

public sealed record EvTripSkipRequestDto(bool Skipped);

public sealed record EvChargingControlDto(bool Enabled, string Mode, double? Amps, string Reason, DateTimeOffset? UpdatedAtUtc, string? CommandProblem);

public sealed record EvChargeSlotDto(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    double PowerWatts,
    double GridKwh,
    double PvKwh,
    double PricePerKwh,
    bool IsPriceEstimated,
    string Reason);

public sealed record EvChargeScenarioDto(string Name, double PvFactor, double GridKwh, double GridCost);

public sealed record EvChargePlanResponseDto(
    DateTimeOffset CalculatedAtUtc,
    string SlotSource,
    double GridKwh,
    double PvKwh,
    double GridCost,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<EvChargeScenarioDto> Scenarios,
    IReadOnlyList<EvChargeSlotDto> Slots);

public sealed record CalendarSyncStatusDto(
    bool Enabled,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSyncAtUtc,
    string? Error,
    int? EventsRead,
    int? TripsFound,
    IReadOnlyList<CalendarUnresolvedDto> Unresolved)
{
    public IReadOnlyList<string> SourceErrors { get; init; } = [];
}

public sealed record CalendarUnresolvedDto(string EventId, string Text, string Query);

public record PlaceRequestDto(string? Label, double? Latitude, double? Longitude);

public sealed record CalendarPlaceRequestDto(string EventId, string? Label, double? Latitude, double? Longitude) : PlaceRequestDto(Label, Latitude, Longitude);

/// <summary>RecurrenceDays: 0 = Sunday ... 6 = Saturday; empty or null for a single trip.</summary>
public sealed record EvTripRequestDto(DateTimeOffset DepartureAtUtc, DateTimeOffset? ReturnAtUtc, string? Destination,
    int[]? RecurrenceDays = null, DateTimeOffset? RecurrenceUntilUtc = null, double? Latitude = null, double? Longitude = null);

public static class EvTripPlanDtoMapper
{
    public static EvTripPlanResponseDto Map(EvTripPlanView view, EvTripActor actor)
    {
        // Other people's trips count for the plan but stay private: only "Fahrt von ..." and the numbers.
        string Destination(EvTrip trip) => actor.MayChange(trip) ? trip.Destination : $"Fahrt von {trip.OwnerName ?? "Admin"}";
        var (plan, car) = (view.Plan, view.Car);
        return new EvTripPlanResponseDto(
        plan.CalculatedAtUtc,
        plan.CurrentSocPercent,
        plan.CurrentSocIsAssumed,
        new EvCarParametersDto(car.UsableCapacityKwh, car.ConsumptionKwhPer100Km, car.MinimumSocPercent, car.MarginPercent, car.ChargePowerWatts,
            view.UsesLearnedConsumption, view.LearnedConsumption is { } learned ? new LearnedConsumptionDto(learned.KwhPer100Km, learned.Drives, learned.TotalKm) : null),
        plan.Trips.Select(requirement => new EvTripDto(
            requirement.Trip.Id,
            Destination(requirement.Trip),
            actor.MayChange(requirement.Trip) ? requirement.Trip.ResolvedAddress : string.Empty,
            requirement.Trip.DepartureAtUtc,
            requirement.ReturnAtUtc,
            requirement.ReturnIsAssumed,
            requirement.Trip.DistanceKm,
            requirement.Trip.DurationMinutes,
            Math.Round(requirement.EnergyKwh, 1),
            Math.Round(requirement.RequiredSocPercent, 1),
            Math.Round(requirement.ExpectedSocAtReturnPercent, 1),
            requirement.LatestChargeStartUtc,
            requirement.IsReachable,
            actor.MayChange(requirement.Trip) ? requirement.Note : requirement.IsReachable ? null : "Bedarf größer als erreichbar.",
            EvTrip.DaysOf(requirement.Trip.RecurrenceDays).Select(day => (int)day).ToArray(),
            requirement.Trip.RecurrenceUntilUtc,
            requirement.Trip.Source,
            actor.MayChange(requirement.Trip) ? requirement.Trip.Subject : null,
            requirement.Trip.OccurrenceDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            requirement.Trip.HasLocationOverride,
            Math.Round(requirement.WinterSurchargePercent, 1),
            requirement.TemperatureCelsius is { } celsius ? Math.Round(celsius, 1) : null)
        {
            CanChange = actor.MayChange(requirement.Trip),
            OwnerName = requirement.Trip.OwnerName
        }).ToArray(),
        plan.Course.Select(point => new EvSocPointDto(point.AtUtc, Math.Round(point.SocPercent, 1))).ToArray(),
        plan.Warnings,
        view.IgnoredTrips.Where(actor.MayChange).Select(trip => new EvIgnoredTripDto(trip.Id, trip.Destination, trip.DepartureAtUtc, trip.Subject,
            trip.OccurrenceDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))).ToArray());
    }
}

public sealed record EvCarParametersDto(double UsableCapacityKwh, double ConsumptionKwhPer100Km, double MinimumSocPercent, double MarginPercent, double ChargePowerWatts,
    bool UsesLearnedConsumption, LearnedConsumptionDto? LearnedConsumption);

public sealed record LearnedConsumptionDto(double KwhPer100Km, int Drives, double TotalKm);

public sealed record EvTripDto(
    int Id,
    string Destination,
    string ResolvedAddress,
    DateTimeOffset DepartureAtUtc,
    DateTimeOffset ReturnAtUtc,
    bool ReturnIsAssumed,
    double DistanceKm,
    double DurationMinutes,
    double EnergyKwh,
    double RequiredSocPercent,
    double ExpectedSocAtReturnPercent,
    DateTimeOffset? LatestChargeStartUtc,
    bool IsReachable,
    string? Note,
    IReadOnlyList<int> RecurrenceDays,
    DateTimeOffset? RecurrenceUntilUtc,
    string Source,
    string? Subject,
    string? OccurrenceDate,
    bool HasLocationOverride,
    double WinterSurchargePercent,
    double? TemperatureCelsius)
{
    public bool CanChange { get; init; }

    public string? OwnerName { get; init; }
}

public sealed record EvIgnoredTripDto(int Id, string Destination, DateTimeOffset DepartureAtUtc, string? Subject, string? OccurrenceDate);

public sealed record EvSocPointDto(DateTimeOffset AtUtc, double SocPercent);

public sealed record EvTripPlanResponseDto(
    DateTimeOffset CalculatedAtUtc,
    double? CurrentSocPercent,
    bool CurrentSocIsAssumed,
    EvCarParametersDto Car,
    IReadOnlyList<EvTripDto> Trips,
    IReadOnlyList<EvSocPointDto> Course,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<EvIgnoredTripDto> IgnoredTrips);
