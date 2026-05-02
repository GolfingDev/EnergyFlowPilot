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

        return endpoints;
    }

    public static async Task<IResult> GetCurrentDecisionAsync(
        ICurrentBatteryDecisionService currentBatteryDecisionService,
        CancellationToken cancellationToken)
    {
        var decisionResult = await currentBatteryDecisionService.CalculateCurrentDecisionAsync(cancellationToken);

        return TypedResults.Ok(CurrentDecisionDtoMapper.Map(decisionResult));
    }
}
