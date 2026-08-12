using TibberVictronController.Dal.HagerEnergy;

namespace TibberVictronController.Api.HagerEnergy;

public static class E3dcEndpoints
{
    public static IEndpointRouteBuilder MapE3dcEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/e3dc/telemetry",
            GetTelemetrySnapshot)
            .WithName("GetE3dcTelemetry")
            .WithTags("E3dc");

        endpoints.MapGet(
            "/api/e3dc/installations",
            GetInstallationsAsync)
            .WithName("GetE3dcInstallations")
            .WithTags("E3dc");

        return endpoints;
    }

    private static IResult GetTelemetrySnapshot(HagerEnergyTelemetrySnapshotStore snapshotStore)
    {
        var snapshot = snapshotStore.Current;

        if (snapshot is null)
        {
            return TypedResults.Ok(new E3dcTelemetryResponseDto(null, null, null, null, null));
        }

        return TypedResults.Ok(new E3dcTelemetryResponseDto(
            snapshot.GridImportWatts,
            snapshot.PvProductionWatts,
            snapshot.BatterySocPercent,
            snapshot.MeasuredAtUtc,
            snapshot.LastSuccessfulPollAtUtc));
    }

    private static async Task<IResult> GetInstallationsAsync(
        HagerEnergyApiClient apiClient,
        CancellationToken cancellationToken)
    {
        try
        {
            var ids = await apiClient.GetInstallationIdsAsync(cancellationToken);
            return TypedResults.Ok(ids);
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.BadRequest(new { message = exception.Message });
        }
        catch (HagerEnergyApiException exception)
        {
            return TypedResults.Problem(exception.Message, statusCode: 502);
        }
    }
}

public sealed record E3dcTelemetryResponseDto(
    int? GridImportWatts,
    int? PvProductionWatts,
    int? BatterySocPercent,
    DateTimeOffset? MeasuredAtUtc,
    DateTimeOffset? LastSuccessfulPollAtUtc);
