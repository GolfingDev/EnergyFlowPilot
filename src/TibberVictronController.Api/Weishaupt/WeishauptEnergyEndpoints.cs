using Microsoft.AspNetCore.Mvc;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Api.Weishaupt;

public static class WeishauptEnergyEndpoints
{
    public static IEndpointRouteBuilder MapWeishauptEnergyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/weishaupt/energy-history", GetAsync).WithTags("Weishaupt");
        return endpoints;
    }

    public static async Task<IResult> GetAsync([FromServices] WeishauptEnergyHistoryService service,
        int? days, DateTimeOffset? endsAtUtc, CancellationToken ct)
    {
        try
        {
            var history = await service.GetAsync(days ?? 7, endsAtUtc, ct);
            return TypedResults.Ok(new WeishauptEnergyHistoryDto(history.StartsAtUtc, history.EndsAtUtc,
                history.Hours.Select(Map).ToArray(), history.PreviousHours.Select(Map).ToArray()));
        }
        catch (ArgumentException exception)
        {
            return TypedResults.BadRequest(new { Error = exception.Message });
        }
    }

    private static WeishauptEnergyHourDto Map(WeishauptEnergyHour hour) => new(hour.StartsAtUtc,
        hour.MeasuredFromUtc, hour.MeasuredToUtc, hour.ElectricalKwh, hour.OutdoorTemperatureCelsius);
}

public sealed record WeishauptEnergyHistoryDto(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc,
    IReadOnlyList<WeishauptEnergyHourDto> Hours, IReadOnlyList<WeishauptEnergyHourDto> PreviousHours);
public sealed record WeishauptEnergyHourDto(DateTimeOffset StartsAtUtc, DateTimeOffset? MeasuredFromUtc,
    DateTimeOffset? MeasuredToUtc, decimal? ElectricalKwh, decimal? OutdoorTemperatureCelsius);
