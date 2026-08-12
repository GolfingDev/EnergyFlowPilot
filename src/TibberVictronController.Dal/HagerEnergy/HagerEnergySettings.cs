namespace TibberVictronController.Dal.HagerEnergy;

public sealed class HagerEnergySettings
{
    public required string ApiBaseUrl { get; init; }
    public required string TokenEndpoint { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? InstallationId { get; init; }
    public required string GridImportJsonPath { get; init; }
    public required string PvProductionJsonPath { get; init; }
    public required string BatterySocJsonPath { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(InstallationId);
}
