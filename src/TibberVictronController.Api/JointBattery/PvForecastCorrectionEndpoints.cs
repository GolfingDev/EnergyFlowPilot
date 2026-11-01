using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

public static class PvForecastCorrectionEndpoints
{
    public static IEndpointRouteBuilder MapPvForecastCorrectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/forecast/pv-correction", GetAsync).WithName("GetPvForecastCorrection").WithTags("Forecast");
        return endpoints;
    }

    private static async Task<IResult> GetAsync(IServiceProvider services, PvForecastLearning learning, IControllerSettingStore settings,
        CancellationToken cancellationToken)
    {
        var zoneId = (await settings.GetSettingAsync(ControllerSettingDefaults.ConsumptionForecastTimeZoneKey, cancellationToken))?.Value ?? "Europe/Berlin";
        var correction = await learning.GetAsync(services, null, TimeZoneInfo.FindSystemTimeZoneById(zoneId), cancellationToken);
        var enabled = bool.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.PvForecastLearnedCorrectionEnabledKey, cancellationToken))?.Value,
            out var value) && value;
        return TypedResults.Ok(new PvForecastCorrectionDto(enabled, correction.IsReady, correction.DaysWithData, PvForecastCorrectionLearner.RequiredDays,
            correction.OverallFactor, correction.FactorByLocalHour.OrderBy(entry => entry.Key)
                .Select(entry => new PvHourFactorDto(entry.Key, entry.Value)).ToArray(),
            correction.CalculatedAtUtc == DateTimeOffset.MinValue ? null : correction.CalculatedAtUtc));
    }
}

public sealed record PvHourFactorDto(int LocalHour, double Factor);

public sealed record PvForecastCorrectionDto(
    bool Enabled,
    bool IsReady,
    int DaysWithData,
    int RequiredDays,
    double OverallFactor,
    IReadOnlyList<PvHourFactorDto> Hours,
    DateTimeOffset? CalculatedAtUtc);
