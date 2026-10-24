using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TibberVictronController.Api.Authentication;

namespace TibberVictronController.Api.Tests;

public sealed class AuthenticationTests
{
    private static ControllerAuthenticationOptions Entra(string publicBase = "https://pi.example.ts.net") => new()
    {
        Mode = "Entra", TenantId = "tenant", ClientId = "client", ClientSecret = "test-only-secret", PublicBaseUrl = publicBase
    };

    private static ClaimsPrincipal User(params string[] roles) => new(new ClaimsIdentity(
        roles.Select(role => new Claim("roles", role)).Append(new Claim("name", "Test")), "test", "name", "roles"));

    private static async Task<(int Status, bool Passed)> RunGuardAsync(string method, string path, ClaimsPrincipal user)
    {
        var passed = false;
        var guard = new AdminWriteGuardMiddleware(_ => { passed = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext { User = user };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        await guard.InvokeAsync(context);
        return (context.Response.StatusCode, passed);
    }

    [Theory]
    [InlineData("POST", "/api/joint-battery/manual-charge")]
    [InlineData("PUT", "/api/settings/victron.dryRun")]
    [InlineData("DELETE", "/api/manual-charge")]
    public async Task ViewerCannotChangeAnything(string method, string path)
    {
        var (status, passed) = await RunGuardAsync(method, path, User(AppRoles.Viewer));
        Assert.False(passed);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task ViewerCanReadAndAdminCanWrite()
    {
        Assert.True((await RunGuardAsync("GET", "/api/settings", User(AppRoles.Viewer))).Passed);
        Assert.True((await RunGuardAsync("POST", "/api/joint-battery/manual-charge", User(AppRoles.Admin))).Passed);
    }

    [Fact]
    public async Task AnonymousWriteIsUnauthorizedAndSignalRNegotiationStaysPossibleForViewers()
    {
        var (status, passed) = await RunGuardAsync("POST", "/api/manual-charge", new ClaimsPrincipal(new ClaimsIdentity()));
        Assert.False(passed);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.True((await RunGuardAsync("POST", "/hubs/dashboard/negotiate", User(AppRoles.Viewer))).Passed);
    }

    [Theory]
    [InlineData("/dashboard?x=1", "/dashboard?x=1")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData(null, "/")]
    public void ReturnUrlStaysLocal(string? input, string expected) => Assert.Equal(expected, PublicHost.SafeReturnUrl(input));

    [Fact]
    public void PublicRequestIsRecognisedDirectlyAndBehindTailscaleFunnel()
    {
        var options = Entra();
        var direct = new DefaultHttpContext();
        direct.Request.Host = new HostString("pi.example.ts.net");
        Assert.True(PublicHost.IsPublicRequest(direct.Request, options));

        var forwarded = new DefaultHttpContext();
        forwarded.Request.Host = new HostString("127.0.0.1", 5094);
        forwarded.Request.Headers["X-Forwarded-Host"] = "pi.example.ts.net";
        Assert.True(PublicHost.IsPublicRequest(forwarded.Request, options));

        var local = new DefaultHttpContext();
        local.Request.Host = new HostString("192.168.1.20");
        Assert.False(PublicHost.IsPublicRequest(local.Request, options));
    }

    [Theory]
    [InlineData(null, "client", "secret", "https://pi.example.ts.net")]
    [InlineData("tenant", "client", null, "https://pi.example.ts.net")]
    [InlineData("tenant", "client", "secret", "http://pi.example.ts.net")]
    [InlineData("tenant", "client", "secret", "https://pi.example.ts.net/app")]
    public void IncompleteEntraConfigurationFailsStartup(string? tenant, string? client, string? secret, string publicBase)
    {
        var options = new ControllerAuthenticationOptions { Mode = "Entra", TenantId = tenant, ClientId = client, ClientSecret = secret, PublicBaseUrl = publicBase };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public async Task EveryEndpointNeedsARoleWhenEntraIsEnabled()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "Entra", ["Authentication:TenantId"] = "tenant", ["Authentication:ClientId"] = "client",
            ["Authentication:ClientSecret"] = "test-only-secret", ["Authentication:PublicBaseUrl"] = "https://pi.example.ts.net"
        }).Build();
        using var services = new ServiceCollection().AddLogging().AddControllerAuthentication(configuration).BuildServiceProvider();
        var fallback = await services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();
        var roles = Assert.Single(fallback!.Requirements.OfType<RolesAuthorizationRequirement>());
        Assert.Equal(new[] { AppRoles.Admin, AppRoles.Viewer }, roles.AllowedRoles.Order());
    }

    [Fact]
    public async Task DisabledModeKeepsTheSystemOpenAsBefore()
    {
        using var services = new ServiceCollection().AddLogging()
            .AddControllerAuthentication(new ConfigurationBuilder().Build()).BuildServiceProvider();
        Assert.Null(await services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync());
        Assert.False(services.GetRequiredService<ControllerAuthenticationOptions>().IsEntra);
    }
}
