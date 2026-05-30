using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Api.Decision;

/// <summary>
/// Maps direct current-decision endpoints and keeps HTTP concerns outside the Decision Engine.
/// </summary>
public static class CurrentDecisionEndpoints
{
    private const int MaximumDecisionLogCount = 5_000;

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

        endpoints.MapGet(
            "/api/decision/history",
            GetDecisionHistoryAsync)
            .WithName("GetCurrentBatteryDecisionHistory")
            .WithTags("Decision");

        return endpoints;
    }

    public static async Task<IResult> GetCurrentDecisionAsync(
        IDecisionLogRepository decisionLogRepository,
        CancellationToken cancellationToken)
    {
        var logEntries = await decisionLogRepository.GetRecentDecisionsAsync(1, cancellationToken);
        var latestLogEntry = logEntries.FirstOrDefault();

        return latestLogEntry is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(CurrentDecisionDtoMapper.MapCurrent(latestLogEntry));
    }

    public static async Task<IResult> GetDecisionLogsAsync(
        IDecisionLogRepository decisionLogRepository,
        int? maxCount,
        CancellationToken cancellationToken)
    {
        var effectiveMaxCount = Math.Clamp(maxCount ?? 20, 1, MaximumDecisionLogCount);
        var logEntries = await decisionLogRepository.GetRecentDecisionsAsync(effectiveMaxCount, cancellationToken);

        return TypedResults.Ok(logEntries.Select(CurrentDecisionDtoMapper.Map).ToArray());
    }

    public static async Task<IResult> GetDecisionHistoryAsync(
        IDecisionLogRepository decisionLogRepository,
        int? hours,
        int? maxCount,
        CancellationToken cancellationToken)
    {
        var effectiveHours = Math.Clamp(hours ?? 24, 1, 24 * 90);
        var effectiveMaxCount = Math.Clamp(maxCount ?? 2_000, 1, MaximumDecisionLogCount);
        var toUtc = DateTimeOffset.UtcNow;
        var fromUtc = toUtc.AddHours(-effectiveHours);
        var logEntries = await decisionLogRepository.GetDecisionsAsync(
            fromUtc,
            toUtc,
            effectiveMaxCount,
            cancellationToken);

        return TypedResults.Ok(logEntries.Select(CurrentDecisionDtoMapper.Map).ToArray());
    }
}
