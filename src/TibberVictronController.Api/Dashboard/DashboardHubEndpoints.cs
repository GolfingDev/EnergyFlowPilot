namespace TibberVictronController.Api.Dashboard;

public static class DashboardHubEndpoints
{
    public static IEndpointRouteBuilder MapDashboardHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<DashboardHub>("/hubs/dashboard");

        return endpoints;
    }
}
