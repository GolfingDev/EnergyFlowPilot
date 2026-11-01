using System.Security.Claims;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Authentication;

/// <summary>
/// Read access for admins only (trips with destinations and times, the calendar, the site address). Changes are
/// already admin-only through <see cref="AdminWriteGuardMiddleware"/>; viewers may see the charging plan but not
/// where and when the family drives. Without sign-in (local operation) everything stays open as before.
/// </summary>
public static class AdminOnlyEndpoints
{
    public static bool IsAdmin(HttpContext http) =>
        http.RequestServices.GetRequiredService<ControllerAuthenticationOptions>() is not { IsEntra: true } || http.User.IsInRole(AppRoles.Admin);

    /// <summary>Admins and drivers: they plan trips (drivers only their own).</summary>
    public static bool IsDriver(HttpContext http) => IsAdmin(http) || http.User.IsInRole(AppRoles.Driver);

    /// <summary>The signed-in user as owner of trips and calendar; without sign-in a local admin.</summary>
    public static EvTripActor Actor(HttpContext http)
    {
        if (http.RequestServices.GetRequiredService<ControllerAuthenticationOptions>() is not { IsEntra: true })
            return new EvTripActor("local", "Lokal", true);
        var user = http.User;
        var id = user.FindFirstValue("oid") ?? user.FindFirstValue("sub") ?? throw new InvalidOperationException("Die Anmeldung enthaelt keine Benutzerkennung.");
        return new EvTripActor(id, user.Identity?.Name ?? user.FindFirstValue("preferred_username") ?? "Unbekannt", user.IsInRole(AppRoles.Admin));
    }

    /// <summary>Mailbox of the signed-in user (sign-in name in Microsoft 365).</summary>
    public static string? Mailbox(HttpContext http) => http.User.FindFirstValue("preferred_username") ?? http.User.FindFirstValue("email");

    public static TBuilder RequireDriver<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) => IsDriver(context.HttpContext)
            ? await next(context)
            : TypedResults.Json(new { message = "Nur fuer Fahrer und Admins sichtbar." }, statusCode: StatusCodes.Status403Forbidden));

    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) => IsAdmin(context.HttpContext)
            ? await next(context)
            : TypedResults.Json(new { message = "Nur mit der Rolle Admin sichtbar." }, statusCode: StatusCodes.Status403Forbidden));
}
