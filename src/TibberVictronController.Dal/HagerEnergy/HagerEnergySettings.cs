namespace TibberVictronController.Dal.HagerEnergy;

/// <summary>
/// Contains Hager Energy API settings needed for read-only telemetry access.
/// </summary>
public sealed class HagerEnergySettings
{
    public required string ApiBaseUrl { get; init; }

    public required string AuthorizationEndpoint { get; init; }

    public required string TokenEndpoint { get; init; }

    public required string RedirectUri { get; init; }

    public required string PostLoginRedirectUrl { get; init; }

    public required string Scope { get; init; }

    public string? OAuthState { get; init; }

    public string? ApiKey { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string? RefreshToken { get; init; }

    public string? AccessToken { get; init; }

    public required string InstallationId { get; init; }

    public required string GridImportJsonPath { get; init; }

    public required string PvProductionJsonPath { get; init; }

    public required string BatterySocJsonPath { get; init; }
}
