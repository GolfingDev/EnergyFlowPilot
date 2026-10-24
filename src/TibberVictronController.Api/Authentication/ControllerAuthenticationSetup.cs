using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace TibberVictronController.Api.Authentication;

/// <summary>
/// Backend-for-frontend sign-in: Entra OpenID Connect (authorization code + PKCE) on the server, session in an
/// HttpOnly cookie. The browser never holds tokens, and every API call is authorized server-side.
/// </summary>
public static class ControllerAuthenticationSetup
{
    public static IServiceCollection AddControllerAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ControllerAuthenticationOptions.SectionName).Get<ControllerAuthenticationOptions>()
            ?? new ControllerAuthenticationOptions();
        options.Validate();
        services.AddSingleton(options);

        if (!options.IsEntra)
        {
            // Previous behaviour: open access. Startup logs a warning; the system must then not be published.
            services.AddAuthorization();
            return services;
        }

        var publicBase = options.PublicBaseUri!.GetLeftPart(UriPartial.Authority);
        services.AddAuthentication(authentication =>
            {
                // Cookie for everything, including challenges: an unauthenticated API call must get 401, not a redirect
                // to Entra that fetch cannot follow. Only /api/auth/login challenges the OpenID Connect scheme explicitly.
                authentication.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = "efp.session";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // Lax keeps the session off cross-site POSTs (CSRF) while normal navigation still works.
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(12);
                cookie.SlidingExpiration = true;
                // API and SignalR callers get status codes; the frontend decides when to start the sign-in.
                cookie.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                cookie.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            })
            .AddOpenIdConnect(oidc =>
            {
                oidc.Authority = $"https://login.microsoftonline.com/{options.TenantId}/v2.0";
                oidc.ClientId = options.ClientId;
                oidc.ClientSecret = options.ClientSecret;
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                oidc.CallbackPath = ControllerAuthenticationOptions.CallbackPath;
                oidc.SignedOutCallbackPath = ControllerAuthenticationOptions.SignedOutCallbackPath;
                oidc.SignedOutRedirectUri = "/";
                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
                oidc.Scope.Add("profile");
                oidc.Scope.Add("email");
                oidc.SaveTokens = false;
                oidc.MapInboundClaims = false;
                oidc.TokenValidationParameters.NameClaimType = "name";
                oidc.TokenValidationParameters.RoleClaimType = "roles";
                // Behind Tailscale Funnel and nginx the app sees plain HTTP; correlation cookies must still be Secure.
                oidc.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                oidc.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                oidc.Events.OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.RedirectUri = publicBase + ControllerAuthenticationOptions.CallbackPath;
                    return Task.CompletedTask;
                };
                oidc.Events.OnRedirectToIdentityProviderForSignOut = context =>
                {
                    context.ProtocolMessage.PostLogoutRedirectUri = publicBase + ControllerAuthenticationOptions.SignedOutCallbackPath;
                    return Task.CompletedTask;
                };
                oidc.Events.OnTokenValidated = context =>
                {
                    // Only accounts with an assigned app role get a session; Entra should also require assignment.
                    if (context.Principal is not { } principal || !(principal.IsInRole(AppRoles.Admin) || principal.IsInRole(AppRoles.Viewer)))
                        context.Fail("Dem Konto ist keine EnergyFlowPilot-Rolle zugewiesen.");
                    return Task.CompletedTask;
                };
                oidc.Events.OnRemoteFailure = context =>
                {
                    // A fixed error page instead of "/" avoids a sign-in loop (SPA -> 401 -> sign-in -> failure -> SPA).
                    context.Response.Redirect("/api/auth/denied");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorization(authorization =>
        {
            authorization.AddPolicy(AppPolicies.Viewer, policy => policy.RequireAuthenticatedUser().RequireRole(AppRoles.Admin, AppRoles.Viewer));
            authorization.AddPolicy(AppPolicies.Admin, policy => policy.RequireAuthenticatedUser().RequireRole(AppRoles.Admin));
            // Every endpoint (API and SignalR) needs at least the viewer role unless it explicitly allows anonymous access.
            authorization.FallbackPolicy = authorization.GetPolicy(AppPolicies.Viewer);
        });
        return services;
    }

    public static WebApplication UseControllerAuthentication(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ControllerAuthenticationOptions>();
        if (!options.IsEntra)
        {
            app.Logger.LogWarning("Anmeldung ist deaktiviert (Authentication:Mode). Das System darf so nicht oeffentlich erreichbar sein.");
            app.UseRouting();
            app.UseAuthorization();
            return app;
        }

        app.Use((context, next) =>
        {
            // Tailscale Funnel terminates HTTPS and forwards plain HTTP via nginx. Requests for the public name are HTTPS
            // from the browser's view; marking them so keeps cookies and redirects consistent.
            if (PublicHost.IsPublicRequest(context.Request, options)) context.Request.Scheme = "https";
            return next(context);
        });
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<AdminWriteGuardMiddleware>();
        return app;
    }
}
