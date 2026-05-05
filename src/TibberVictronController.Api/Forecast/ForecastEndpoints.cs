using Microsoft.AspNetCore.Mvc;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Dal.Weather;

namespace TibberVictronController.Api.Forecast;

/// <summary>
/// Maps forecast API endpoints and keeps HTTP concerns outside the Decision Engine.
/// </summary>
public static class ForecastEndpoints
{
    private const int MinimumForecastHours = 1;
    private const int MaximumForecastHours = 72;

    /// <summary>
    /// Registers Battery Decision Engine forecast endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapForecastEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/forecast",
            GetForecastAsync)
            .WithName("GetBatteryForecast")
            .WithTags("Forecast");

        return endpoints;
    }

    /// <summary>
    /// Returns the forecast DTO for the requested UTC start and forecast horizon.
    /// </summary>
    public static async Task<IResult> GetForecastAsync(
        DateTimeOffset startsAtUtc,
        int hours,
        [FromServices]
        IBatteryForecastService forecastService,
        CancellationToken cancellationToken)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero)
        {
            return TypedResults.BadRequest(new ForecastErrorDto("Der Forecast-Start muss in UTC angegeben sein."));
        }

        if (hours is < MinimumForecastHours or > MaximumForecastHours)
        {
            return TypedResults.BadRequest(new ForecastErrorDto("Der Forecast-Zeitraum muss zwischen 1 und 72 Stunden liegen."));
        }

        var endsAtUtc = startsAtUtc.AddHours(hours);

        try
        {
            var forecastResult = await forecastService.CalculateForecastAsync(startsAtUtc, endsAtUtc, cancellationToken);
            var forecastDto = BatteryForecastDtoMapper.Map(forecastResult);

            return TypedResults.Ok(forecastDto);
        }
        catch (ForecastSolarApiException exception)
        {
            return TypedResults.BadRequest(new ForecastErrorDto(exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.BadRequest(new ForecastErrorDto(exception.Message));
        }
        catch (ArgumentException exception)
        {
            return TypedResults.BadRequest(new ForecastErrorDto(exception.Message));
        }
    }
}
