using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace TibberVictronController.Api.Authentication;

public sealed record CurrentUserDto(bool AuthenticationEnabled, string? Name, string? Email, IReadOnlyList<string> Roles, bool IsAdmin);

public static class PublicHost
{
    /// <summary>True for requests addressed to the public name, directly or as forwarded by Tailscale Funnel.</summary>
    public static bool IsPublicRequest(HttpRequest request, ControllerAuthenticationOptions options)
    {
        if (options.PublicBaseUri is not { } uri) return false;
        var forwardedHost = request.Headers["X-Forwarded-Host"].ToString().Split(',')[0].Trim();
        return string.Equals(request.Host.Host, uri.Host, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(forwardedHost.Split(':')[0], uri.Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Only local paths are accepted as return target; anything else would be an open redirect.</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal) &&
        !returnUrl.StartsWith("/\\", StringComparison.Ordinal) ? returnUrl : "/";
}

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/auth/me", (HttpContext context, ControllerAuthenticationOptions options) =>
        {
            if (!options.IsEntra)
                return Results.Ok(new CurrentUserDto(false, null, null, [], true));
            var user = context.User;
            if (user.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            var roles = user.FindAll("roles").Select(c => c.Value).Where(r => r is AppRoles.Admin or AppRoles.Viewer).Distinct().ToArray();
            return Results.Ok(new CurrentUserDto(true, user.Identity.Name, user.FindFirstValue("preferred_username") ?? user.FindFirstValue("email"),
                roles, user.IsInRole(AppRoles.Admin)));
        }).AllowAnonymous().WithTags("Auth");

        endpoints.MapGet("/api/auth/login", (HttpContext context, ControllerAuthenticationOptions options, string? returnUrl, int? hop) =>
        {
            var target = PublicHost.SafeReturnUrl(returnUrl);
            if (!options.IsEntra) return Results.Redirect(target);
            // The session cookie belongs to the public name registered in Entra. A local address (e.g. the Pi's IP) is sent
            // there once; the hop marker prevents a loop if a proxy hides the public host name.
            if (!PublicHost.IsPublicRequest(context.Request, options) && hop is null)
                return Results.Redirect($"{options.PublicBaseUri!.GetLeftPart(UriPartial.Authority)}/api/auth/login?returnUrl={Uri.EscapeDataString(target)}&hop=1");
            return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OpenIdConnectDefaults.AuthenticationScheme]);
        }).AllowAnonymous().WithTags("Auth");

        // POST (submitted as a form by the frontend): a GET logout could be triggered by any foreign page.
        endpoints.MapPost("/api/auth/logout", (ControllerAuthenticationOptions options) => options.IsEntra
            ? Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme])
            : Results.Redirect("/")).AllowAnonymous().WithTags("Auth");

        endpoints.MapGet("/api/auth/denied", () => Results.Content("""
            <!doctype html><html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Kein Zugriff</title></head>
            <body style="font-family:system-ui,sans-serif;max-width:36rem;margin:3rem auto;padding:0 1rem;line-height:1.5">
            <h1>Kein Zugriff auf EnergyFlowPilot</h1>
            <p>Die Anmeldung war nicht erfolgreich oder Ihrem Konto ist keine Rolle zugewiesen. Bitte wenden Sie sich an den Betreiber.</p>
            <form method="post" action="/api/auth/logout"><button type="submit">Mit anderem Konto anmelden</button></form>
            </body></html>
            """, "text/html; charset=utf-8")).AllowAnonymous().WithTags("Auth");

        return endpoints;
    }
}
