using Microsoft.AspNetCore.Mvc;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Homematic;

/// <summary>
/// Exposes the read-only Homematic climate history for the heating view.
/// </summary>
public static class HomematicEndpoints
{
    public static IEndpointRouteBuilder MapHomematicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/homematic/history",
            GetHistoryAsync)
            .WithName("GetHomematicHistory")
            .WithTags("Homematic");

        return endpoints;
    }

    public static async Task<IResult> GetHistoryAsync(
        string? range,
        [FromServices]
        IHomematicClimateHistoryService historyService,
        CancellationToken cancellationToken)
    {
        HomematicHistoryRange? historyRange = (range ?? "day").ToLowerInvariant() switch
        {
            "day" => HomematicHistoryRange.Day,
            "week" => HomematicHistoryRange.Week,
            "month" => HomematicHistoryRange.Month,
            _ => null
        };

        if (historyRange is null)
        {
            return TypedResults.BadRequest(new HomematicErrorDto("Der Zeitraum muss day, week oder month sein."));
        }

        var history = await historyService.GetHistoryAsync(historyRange.Value, cancellationToken);

        return TypedResults.Ok(Map(history));
    }

    private static HomematicHistoryResponseDto Map(HomematicClimateHistory history)
    {
        return new HomematicHistoryResponseDto(
            history.StartsAtUtc,
            history.EndsAtUtc,
            (int)history.BucketDuration.TotalMinutes,
            history.IsCollectionEnabled,
            history.LastPolledAtUtc,
            new HomematicSummaryDto(
                history.Summary.OutdoorTemperatureCelsius,
                history.Summary.RadiatorValveOpeningPercent,
                history.Summary.UnderfloorValveOpeningPercent,
                history.Summary.RoomCount,
                history.Summary.RoomsBelowSetPointCount),
            history.Series
                .Select(series => new HomematicSeriesDto(
                    series.ChannelAddress,
                    series.DisplayName,
                    series.Kind.ToString(),
                    series.IsRoom,
                    series.Points
                        .Select(point => new HomematicPointDto(
                            point.BucketStartsAtUtc,
                            point.ActualTemperatureCelsius,
                            point.SetPointTemperatureCelsius,
                            point.ValveOpeningPercent))
                        .ToArray()))
                .ToArray(),
            history.ValveVersusOutdoor
                .Select(point => new HomematicValveOutdoorPointDto(
                    point.BucketStartsAtUtc,
                    point.OutdoorTemperatureCelsius,
                    point.RadiatorValveOpeningPercent,
                    point.UnderfloorValveOpeningPercent))
                .ToArray());
    }
}

public sealed record HomematicHistoryResponseDto(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    int BucketMinutes,
    bool IsCollectionEnabled,
    DateTimeOffset? LastPolledAtUtc,
    HomematicSummaryDto Summary,
    IReadOnlyList<HomematicSeriesDto> Series,
    IReadOnlyList<HomematicValveOutdoorPointDto> ValveVersusOutdoor);

public sealed record HomematicSummaryDto(
    decimal? OutdoorTemperatureCelsius,
    decimal? RadiatorValveOpeningPercent,
    decimal? UnderfloorValveOpeningPercent,
    int RoomCount,
    int RoomsBelowSetPointCount);

public sealed record HomematicSeriesDto(
    string ChannelAddress,
    string DisplayName,
    string Kind,
    bool IsRoom,
    IReadOnlyList<HomematicPointDto> Points);

public sealed record HomematicPointDto(
    DateTimeOffset BucketStartsAtUtc,
    decimal? ActualTemperatureCelsius,
    decimal? SetPointTemperatureCelsius,
    decimal? ValveOpeningPercent);

public sealed record HomematicValveOutdoorPointDto(
    DateTimeOffset BucketStartsAtUtc,
    decimal OutdoorTemperatureCelsius,
    decimal? RadiatorValveOpeningPercent,
    decimal? UnderfloorValveOpeningPercent);

public sealed record HomematicErrorDto(string Message);
