using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Api.Forecast;

public static class TariffComparisonEndpoints
{
    public static IEndpointRouteBuilder MapTariffComparisonEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/electricity-prices/compare", GetAsync).WithTags("Stromtarife");
        return endpoints;
    }
    public static async Task<Results<Ok<TariffComparisonDto>, BadRequest<ForecastErrorDto>>> GetAsync(
        decimal annualConsumptionKwh, string? postalCode, int? days, DynamicTariffComparisonService service, CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.CompareAsync(new(annualConsumptionKwh, postalCode, days), cancellationToken);
            return TypedResults.Ok(new TariffComparisonDto(result.StartsAtUtc, result.EndsAtUtc, result.PriceSlots,
                result.AverageNetMarketPricePerKwh, result.AnnualConsumptionKwh, result.PostalCode,
                result.Offers.Select(o => new TariffComparisonRowDto(o.Name, o.GrossWorkPricePerKwh, o.EstimatedAnnualCost, o.ExclusionReason, o.Source, o.VerifiedAt)).ToArray()));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or HttpRequestException)
        { return TypedResults.BadRequest(new ForecastErrorDto(error.Message)); }
    }
}
public sealed record TariffComparisonDto(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, int PriceSlots,
    decimal AverageNetMarketPricePerKwh, decimal AnnualConsumptionKwh, string? PostalCode, IReadOnlyList<TariffComparisonRowDto> Offers);
public sealed record TariffComparisonRowDto(string Name, decimal? GrossWorkPricePerKwh, decimal? EstimatedAnnualCost,
    string? ExclusionReason, string? Source, DateOnly? VerifiedAt);
