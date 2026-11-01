using TibberVictronController.Api.Authentication;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.EvCharging;

public static class EvTripEndpoints
{
    public static IEndpointRouteBuilder MapEvTripEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ev-charging/trips", GetPlanAsync).WithName("GetEvTripPlan").WithTags("EvCharging").RequireAdmin();
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
        endpoints.MapGet("/api/ev-charging/calendar", GetCalendarStatusAsync).WithName("GetCalendarSyncStatus").WithTags("EvCharging").RequireAdmin();
        endpoints.MapPost("/api/ev-charging/calendar/sync", SyncCalendarAsync).WithName("SyncCalendar").WithTags("EvCharging");
        return endpoints;
    }

    private static async Task<IResult> GetPlanAsync(HttpContext http, EvTripPlanningService planning, ControllerAuthenticationOptions authentication,
        CancellationToken cancellationToken)
    {
        try
        {
            // Appointment subjects are private; viewers only see the destination.
            var view = await planning.GetPlanAsync(cancellationToken);
            return TypedResults.Ok(EvTripPlanDtoMapper.Map(view, IsAdmin(http, authentication)));
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.Problem(exception.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> AddTripAsync(EvTripRequestDto request, EvTripPlanningService planning, CancellationToken cancellationToken)
    {
        try
        {
            if (request.RecurrenceDays?.Any(day => day is < 0 or > 6) == true)
                return TypedResults.BadRequest(new { message = "Wochentage muessen zwischen 0 (Sonntag) und 6 (Samstag) liegen." });
            var trip = await planning.AddTripAsync(new EvTripRequest(request.DepartureAtUtc, request.ReturnAtUtc, request.Destination ?? string.Empty,
                request.RecurrenceDays?.Select(day => (DayOfWeek)day).ToArray(), request.RecurrenceUntilUtc)
            {
                Target = request.Latitude is { } latitude && request.Longitude is { } longitude ? new GeoPoint(latitude, longitude) : null
            }, cancellationToken);
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

    private static async Task<IResult> DeleteTripAsync(int id, EvTripPlanningService planning, CancellationToken cancellationToken) =>
        await planning.DeleteTripAsync(id, cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound(new { message = "Die Fahrt gibt es nicht (mehr)." });

    private static async Task<IResult> SetIgnoredAsync(int id, EvTripIgnoreRequestDto request, EvTripPlanningService planning, CancellationToken cancellationToken) =>
        await planning.SetIgnoredAsync(id, request.Ignored, cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound(new { message = "Die Fahrt gibt es nicht (mehr)." });

    private static async Task<IResult> SetOccurrenceSkippedAsync(int id, string date, EvTripSkipRequestDto request, EvTripPlanningService planning,
        CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
            return TypedResults.BadRequest(new { message = "Das Datum muss im Format JJJJ-MM-TT angegeben sein." });
        return await planning.SetOccurrenceSkippedAsync(id, day, request.Skipped, cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound(new { message = "Die Serie gibt es nicht (mehr)." });
    }

    private static async Task<IResult> SetLocationAsync(int id, PlaceRequestDto request, EvTripPlanningService planning, CancellationToken cancellationToken)
    {
        try
        {
            return await planning.SetLocationAsync(id, request.Label ?? string.Empty, Target(request), cancellationToken)
                ? TypedResults.NoContent()
                : TypedResults.NotFound(new { message = "Die Fahrt gibt es nicht (mehr)." });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return TypedResults.BadRequest(new { message = exception.Message });
        }
    }

    // The appointment comes from the last sync on the server, not from the browser.
    private static async Task<IResult> SetCalendarPlaceAsync(CalendarPlaceRequestDto request, CalendarSyncState state, CalendarTripSyncService sync,
        CancellationToken cancellationToken)
    {
        var appointment = state.Current.Result?.Unresolved.FirstOrDefault(entry => entry.EventId == request.EventId);
        if (appointment is null) return TypedResults.NotFound(new { message = "Der Termin ist nicht mehr offen; bitte neu abgleichen." });
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
        var isAdmin = IsAdmin(http, authentication);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        return TypedResults.Ok(new CalendarSyncStatusDto(enabled, attemptAt, result?.SyncedAtUtc, error, result?.EventsRead, result?.TripsFound,
            isAdmin ? (result?.Unresolved ?? []).Select(entry => new CalendarUnresolvedDto(entry.EventId, entry.Text(zone), entry.Query)).ToArray() : []));
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

    private static bool IsAdmin(HttpContext http, ControllerAuthenticationOptions authentication) =>
        !authentication.IsEntra || http.User.IsInRole(AppRoles.Admin);
}

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
    IReadOnlyList<CalendarUnresolvedDto> Unresolved);

public sealed record CalendarUnresolvedDto(string EventId, string Text, string Query);

public record PlaceRequestDto(string? Label, double? Latitude, double? Longitude);

public sealed record CalendarPlaceRequestDto(string EventId, string? Label, double? Latitude, double? Longitude) : PlaceRequestDto(Label, Latitude, Longitude);

/// <summary>RecurrenceDays: 0 = Sunday ... 6 = Saturday; empty or null for a single trip.</summary>
public sealed record EvTripRequestDto(DateTimeOffset DepartureAtUtc, DateTimeOffset? ReturnAtUtc, string? Destination,
    int[]? RecurrenceDays = null, DateTimeOffset? RecurrenceUntilUtc = null, double? Latitude = null, double? Longitude = null);

public static class EvTripPlanDtoMapper
{
    public static EvTripPlanResponseDto Map(EvTripPlanView view, bool isAdmin)
    {
        var (plan, car) = (view.Plan, view.Car);
        return new EvTripPlanResponseDto(
        plan.CalculatedAtUtc,
        plan.CurrentSocPercent,
        plan.CurrentSocIsAssumed,
        new EvCarParametersDto(car.UsableCapacityKwh, car.ConsumptionKwhPer100Km, car.MinimumSocPercent, car.MarginPercent, car.ChargePowerWatts,
            view.UsesLearnedConsumption, view.LearnedConsumption is { } learned ? new LearnedConsumptionDto(learned.KwhPer100Km, learned.Drives, learned.TotalKm) : null),
        plan.Trips.Select(requirement => new EvTripDto(
            requirement.Trip.Id,
            requirement.Trip.Destination,
            requirement.Trip.ResolvedAddress,
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
            requirement.Note,
            EvTrip.DaysOf(requirement.Trip.RecurrenceDays).Select(day => (int)day).ToArray(),
            requirement.Trip.RecurrenceUntilUtc,
            requirement.Trip.Source,
            isAdmin ? requirement.Trip.Subject : null,
            requirement.Trip.OccurrenceDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            requirement.Trip.HasLocationOverride,
            Math.Round(requirement.WinterSurchargePercent, 1),
            requirement.TemperatureCelsius is { } celsius ? Math.Round(celsius, 1) : null)).ToArray(),
        plan.Course.Select(point => new EvSocPointDto(point.AtUtc, Math.Round(point.SocPercent, 1))).ToArray(),
        plan.Warnings,
        view.IgnoredTrips.Select(trip => new EvIgnoredTripDto(trip.Id, trip.Destination, trip.DepartureAtUtc, isAdmin ? trip.Subject : null,
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
    double? TemperatureCelsius);

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
