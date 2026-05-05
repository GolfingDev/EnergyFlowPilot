using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Api.Decision;

/// <summary>
/// Maps direct current-decision endpoints and keeps HTTP concerns outside the Decision Engine.
/// </summary>
public static class CurrentDecisionEndpoints
{
    public static IEndpointRouteBuilder MapCurrentDecisionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/decision/current",
            GetCurrentDecisionAsync)
            .WithName("GetCurrentBatteryDecision")
            .WithTags("Decision");

        endpoints.MapGet(
            "/api/decision/logs",
            GetDecisionLogsAsync)
            .WithName("GetCurrentBatteryDecisionLogs")
            .WithTags("Decision");

        return endpoints;
    }

    public static async Task<IResult> GetCurrentDecisionAsync(
        ICurrentBatteryDecisionService currentBatteryDecisionService,
        CancellationToken cancellationToken)
    {
        var decisionResult = await currentBatteryDecisionService.CalculateCurrentDecisionAsync(cancellationToken);

        return TypedResults.Ok(CurrentDecisionDtoMapper.Map(decisionResult));
    }

    public static async Task<IResult> GetDecisionLogsAsync(
        IDecisionLogRepository decisionLogRepository,
        int? maxCount,
        CancellationToken cancellationToken)
    {
        var effectiveMaxCount = Math.Clamp(maxCount ?? 20, 1, 100);
        var logEntries = await decisionLogRepository.GetRecentDecisionsAsync(effectiveMaxCount, cancellationToken);

        return TypedResults.Ok(logEntries.Select(CurrentDecisionDtoMapper.Map).ToArray());
    }
}
