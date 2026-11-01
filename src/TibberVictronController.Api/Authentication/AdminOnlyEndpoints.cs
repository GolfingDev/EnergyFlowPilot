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

    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) => IsAdmin(context.HttpContext)
            ? await next(context)
            : TypedResults.Json(new { message = "Nur mit der Rolle Admin sichtbar." }, statusCode: StatusCodes.Status403Forbidden));
}
