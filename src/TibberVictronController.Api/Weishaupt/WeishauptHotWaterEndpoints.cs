using Microsoft.AspNetCore.Mvc;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Api.Weishaupt;

public static class WeishauptHotWaterEndpoints
{
    public static IEndpointRouteBuilder MapWeishauptHotWaterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/weishaupt/hot-water", GetReportAsync).WithTags("Weishaupt");
        return endpoints;
    }

    public static async Task<IResult> GetReportAsync([FromServices] WeishauptHotWaterReportService service, CancellationToken ct)
    {
        var report = await service.GetReportAsync(ct);
        return TypedResults.Ok(new HotWaterReportDto(report.GeneratedAtUtc, report.IsRaised, report.IsSuspended,
            "Keine separate Strommessung: Preisvorteil ist eine Modellschaetzung je vergleichbarer elektrischer kWh, keine gemessene Euro-Ersparnis.",
            Map(report.Plan), report.Episodes.Select(a => new HotWaterEpisodeDto(a.Episode.Id, a.Episode.StartedAtUtc,
                a.Episode.EndedAtUtc, a.Episode.Trigger, a.Episode.OriginalCelsius, a.Episode.RaisedCelsius,
                a.Episode.WriteConfirmed, Map(a.Episode.Plan)!, a.TargetReachedAtUtc, a.NextHeatingAtUtc,
                a.ReserveLostAtUtc, a.TemperatureAtNeedCelsius, a.BridgedToNeed, a.HasDataGaps, a.Assessment)).ToArray(),
            report.Samples.Select(s => new HotWaterPointDto(s.MeasuredAtUtc, s.HotWaterTemperatureCelsius, s.OperatingStatusCode)).ToArray()));
    }

    private static HotWaterPlanDto? Map(WeishauptHotWaterPricePlan? p) => p is null ? null :
        new(p.NeedByUtc, p.StartsAtUtc, p.EndsAtUtc, p.WindowPricePerKwh, p.ReferencePricePerKwh,
            p.EstimatedAdvantagePerKwh, p.HoldHours, p.ChargeMinutes, p.Reason);
}

public sealed record HotWaterReportDto(DateTimeOffset GeneratedAtUtc, bool IsRaised, bool IsSuspended,
    string CostAssessment, HotWaterPlanDto? Plan, IReadOnlyList<HotWaterEpisodeDto> Episodes, IReadOnlyList<HotWaterPointDto> Samples);
public sealed record HotWaterPlanDto(DateTimeOffset NeedByUtc, DateTimeOffset? StartsAtUtc, DateTimeOffset? EndsAtUtc,
    decimal? WindowPricePerKwh, decimal? ReferencePricePerKwh, decimal? EstimatedAdvantagePerKwh,
    decimal HoldHours, int ChargeMinutes, string Reason);
public sealed record HotWaterEpisodeDto(Guid Id, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc,
    string Trigger, decimal OriginalCelsius, decimal RaisedCelsius, bool WriteConfirmed, HotWaterPlanDto Plan,
    DateTimeOffset? TargetReachedAtUtc, DateTimeOffset? NextHeatingAtUtc, DateTimeOffset? ReserveLostAtUtc,
    decimal? TemperatureAtNeedCelsius, bool? BridgedToNeed, bool HasDataGaps, string Assessment);
public sealed record HotWaterPointDto(DateTimeOffset MeasuredAtUtc, decimal TemperatureCelsius, ushort OperatingStatusCode);
