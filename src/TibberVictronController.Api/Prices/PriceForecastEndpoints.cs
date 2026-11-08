using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Api.Prices;

public static class PriceForecastEndpoints
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    public static IEndpointRouteBuilder MapPriceForecastEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/forecast/price-forecast-accuracy", GetAsync).WithName("GetPriceForecastAccuracy").WithTags("Forecast");
        return endpoints;
    }

    private static async Task<IResult> GetAsync(IPriceForecastSnapshotRepository repository, PriceForecastShadowState state, IControllerSettingStore settings,
        IUtcClock clock, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var zoneId = (await settings.GetSettingAsync(ControllerSettingDefaults.ConsumptionForecastTimeZoneKey, cancellationToken))?.Value ?? "Europe/Berlin";
        var rows = PriceForecastAccuracy.Evaluate(await repository.GetAsync(now - Window, now, cancellationToken), TimeZoneInfo.FindSystemTimeZoneById(zoneId));
        var status = state.Status;
        return TypedResults.Ok(new PriceForecastAccuracyDto(status.LastFetchAtUtc, status.ForecastUntilUtc, status.ForecastQuarters, status.Problem,
            PriceForecastAccuracy.CheapestQuarters / 4,
            rows.Select(row => new PriceForecastAccuracyRowDto(row.LeadDays, row.Quarters, row.Days, Math.Round(row.MeanAbsoluteErrorCent, 2),
                Math.Round(row.BiasCent, 2), row.CheapestHoursExtraCostCent is { } extra ? Math.Round(extra, 2) : null)).ToArray())
        {
            MissingSettings = status.MissingSettings
        });
    }
}

public sealed record PriceForecastAccuracyRowDto(int LeadDays, int Quarters, int Days, double MeanAbsoluteErrorCent, double BiasCent,
    double? CheapestHoursExtraCostCent);

public sealed record PriceForecastAccuracyDto(
    DateTimeOffset? LastFetchAtUtc,
    DateTimeOffset? ForecastUntilUtc,
    int ForecastQuarters,
    string? Problem,
    int CheapestHours,
    IReadOnlyList<PriceForecastAccuracyRowDto> Rows)
{
    public IReadOnlyList<string> MissingSettings { get; init; } = [];
}
