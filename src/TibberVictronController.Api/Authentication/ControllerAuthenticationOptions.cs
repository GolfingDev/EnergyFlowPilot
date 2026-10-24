namespace TibberVictronController.Api.Authentication;

/// <summary>Entra app roles. Their values must match the app roles defined in the Entra app registration.</summary>
public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";
}

public static class AppPolicies
{
    public const string Viewer = "EnergyFlowPilot.Viewer";
    public const string Admin = "EnergyFlowPilot.Admin";
}

/// <summary>
/// Sign-in configuration. Read from configuration (environment file on the Pi), not from the settings database:
/// it is needed before anyone can sign in, and its secret must never be editable through the web UI.
/// </summary>
public sealed class ControllerAuthenticationOptions
{
    public const string SectionName = "Authentication";
    public const string CallbackPath = "/api/auth/signin-oidc";
    public const string SignedOutCallbackPath = "/api/auth/signout-callback-oidc";

    /// <summary>"Entra" enables sign-in; anything else keeps the previous open access (local operation only).</summary>
    public string Mode { get; set; } = "Disabled";
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    /// <summary>The single public HTTPS address (e.g. the Tailscale Funnel name) registered as redirect URI in Entra.</summary>
    public string? PublicBaseUrl { get; set; }

    public bool IsEntra => string.Equals(Mode, "Entra", StringComparison.OrdinalIgnoreCase);

    public Uri? PublicBaseUri => Uri.TryCreate(PublicBaseUrl?.TrimEnd('/'), UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>Fails startup explicitly instead of running a half-configured, possibly open public system.</summary>
    public void Validate()
    {
        if (!IsEntra) return;
        if (string.IsNullOrWhiteSpace(TenantId) || string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
            throw new InvalidOperationException("Die Entra-Anmeldung ist aktiviert, aber Mandant, Client-ID oder Client-Geheimnis fehlen.");
        if (PublicBaseUri is not { Scheme: "https" } uri || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query))
            throw new InvalidOperationException("Die Entra-Anmeldung benoetigt eine oeffentliche HTTPS-Basisadresse ohne Pfad, z. B. https://pi.tailnet.ts.net.");
    }
}
