using TibberVictronController.Web.Models;
using TibberVictronController.Web.Services;

namespace TibberVictronController.Web.Controllers;

public static class DashboardApiController
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/dashboard", async (IDashboardQueryService dashboardService, CancellationToken ct) =>
        {
            var dashboard = await dashboardService.GetAsync(ct);
            return Results.Ok(new DashboardResponse(
                dashboard.CurrentState,
                dashboard.Decisions,
                dashboard.StateHistory,
                dashboard.TibberPrices,
                dashboard.LastStateUpdateUtc,
                dashboard.IsStateStale));
        });
    }
}
