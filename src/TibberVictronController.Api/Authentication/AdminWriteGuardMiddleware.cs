using Microsoft.AspNetCore.Authorization;

namespace TibberVictronController.Api.Authentication;

/// <summary>
/// Viewers may read everything but change nothing. Instead of annotating each endpoint (and risking a forgotten one),
/// every state-changing HTTP method requires the admin role here, centrally.
/// </summary>
public sealed class AdminWriteGuardMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (IsReadOnlyMethod(request.Method) || IsExempt(request.Path) ||
            context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null ||
            context.User.IsInRole(AppRoles.Admin))
            return next(context);

        context.Response.StatusCode = context.User.Identity?.IsAuthenticated == true
            ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { message = "Aenderungen sind nur mit der Rolle Admin erlaubt." });
    }

    private static bool IsReadOnlyMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    // SignalR negotiates with POST but the dashboard hub offers no client-callable methods; sign-in/out has its own rules.
    private static bool IsExempt(PathString path) =>
        path.StartsWithSegments("/hubs") || path.StartsWithSegments("/api/hubs") || path.StartsWithSegments("/api/auth");
}
