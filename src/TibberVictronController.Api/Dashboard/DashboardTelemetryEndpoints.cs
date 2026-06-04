using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Api.Dashboard;

/// <summary>
/// Maps lightweight dashboard telemetry endpoints used as a live-update fallback.
/// </summary>
public static class DashboardTelemetryEndpoints
{
    public static IEndpointRouteBuilder MapDashboardTelemetryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/dashboard/telemetry",
            GetDashboardTelemetryAsync)
            .WithName("GetDashboardTelemetry")
            .WithTags("Dashboard");

        return endpoints;
    }

    public static async Task<IResult> GetDashboardTelemetryAsync(
        ICurrentSiteTelemetryProvider siteTelemetryProvider,
        IBatteryStateProvider batteryStateProvider,
        CancellationToken cancellationToken)
    {
        var siteTelemetry = await siteTelemetryProvider.GetCurrentSiteTelemetryAsync(cancellationToken);
        var batteryState = await batteryStateProvider.GetCurrentBatteryStateAsync(cancellationToken);
        var measuredAtUtc = siteTelemetry.MeasuredAtUtc <= batteryState.MeasuredAtUtc
            ? siteTelemetry.MeasuredAtUtc
            : batteryState.MeasuredAtUtc;
        var houseConsumptionWatts = Math.Max(
            0,
            siteTelemetry.CurrentPvProductionWatts +
            siteTelemetry.CurrentGridImportWatts -
            (siteTelemetry.CurrentBatteryPowerWatts ?? 0));

        return TypedResults.Ok(new DashboardTelemetryUpdateDto(
            siteTelemetry.CurrentGridImportWatts,
            houseConsumptionWatts,
            siteTelemetry.CurrentBatteryPowerWatts,
            batteryState.StateOfChargePercent,
            measuredAtUtc));
    }
}
