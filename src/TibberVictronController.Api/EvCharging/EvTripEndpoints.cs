using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.EvCharging;

public static class EvTripEndpoints
{
    public static IEndpointRouteBuilder MapEvTripEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ev-charging/trips", GetPlanAsync).WithName("GetEvTripPlan").WithTags("EvCharging");
        endpoints.MapPost("/api/ev-charging/trips", AddTripAsync).WithName("AddEvTrip").WithTags("EvCharging");
        endpoints.MapDelete("/api/ev-charging/trips/{id:int}", DeleteTripAsync).WithName("DeleteEvTrip").WithTags("EvCharging");
        return endpoints;
    }

    private static async Task<IResult> GetPlanAsync(EvTripPlanningService planning, CancellationToken cancellationToken)
    {
        try
        {
            var (plan, car) = await planning.GetPlanAsync(cancellationToken);
            return TypedResults.Ok(EvTripPlanDtoMapper.Map(plan, car));
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
                request.RecurrenceDays?.Select(day => (DayOfWeek)day).ToArray(), request.RecurrenceUntilUtc), cancellationToken);
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
}

/// <summary>RecurrenceDays: 0 = Sunday ... 6 = Saturday; empty or null for a single trip.</summary>
public sealed record EvTripRequestDto(DateTimeOffset DepartureAtUtc, DateTimeOffset? ReturnAtUtc, string? Destination,
    int[]? RecurrenceDays = null, DateTimeOffset? RecurrenceUntilUtc = null);

public static class EvTripPlanDtoMapper
{
    public static EvTripPlanResponseDto Map(EvTripPlan plan, EvCarParameters car) => new(
        plan.CalculatedAtUtc,
        plan.CurrentSocPercent,
        plan.CurrentSocIsAssumed,
        new EvCarParametersDto(car.UsableCapacityKwh, car.ConsumptionKwhPer100Km, car.MinimumSocPercent, car.MarginPercent, car.ChargePowerWatts),
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
            requirement.Trip.RecurrenceUntilUtc)).ToArray(),
        plan.Course.Select(point => new EvSocPointDto(point.AtUtc, Math.Round(point.SocPercent, 1))).ToArray(),
        plan.Warnings);
}

public sealed record EvCarParametersDto(double UsableCapacityKwh, double ConsumptionKwhPer100Km, double MinimumSocPercent, double MarginPercent, double ChargePowerWatts);

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
    DateTimeOffset? RecurrenceUntilUtc);

public sealed record EvSocPointDto(DateTimeOffset AtUtc, double SocPercent);

public sealed record EvTripPlanResponseDto(
    DateTimeOffset CalculatedAtUtc,
    double? CurrentSocPercent,
    bool CurrentSocIsAssumed,
    EvCarParametersDto Car,
    IReadOnlyList<EvTripDto> Trips,
    IReadOnlyList<EvSocPointDto> Course,
    IReadOnlyList<string> Warnings);
