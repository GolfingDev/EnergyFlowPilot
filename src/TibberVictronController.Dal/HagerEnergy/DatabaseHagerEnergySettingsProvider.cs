using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.HagerEnergy;

/// <summary>
/// Reads Hager Energy API settings from persisted controller settings.
/// </summary>
public sealed class DatabaseHagerEnergySettingsProvider
{
    private readonly IControllerSettingStore controllerSettingStore;

    public DatabaseHagerEnergySettingsProvider(IControllerSettingStore controllerSettingStore)
    {
        this.controllerSettingStore = controllerSettingStore;
    }

    public async Task<HagerEnergySettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return new HagerEnergySettings
        {
            ApiBaseUrl = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyApiBaseUrlKey, "Die Hager-Energy-API-Basis-URL ist nicht konfiguriert.", cancellationToken),
            AuthorizationEndpoint = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyAuthorizationEndpointKey, "Der Hager-Energy-Authorization-Endpunkt ist nicht konfiguriert.", cancellationToken),
            TokenEndpoint = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyTokenEndpointKey, "Der Hager-Energy-Token-Endpunkt ist nicht konfiguriert.", cancellationToken),
            RedirectUri = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyRedirectUriKey, "Die Hager-Energy-Redirect-URI ist nicht konfiguriert.", cancellationToken),
            PostLoginRedirectUrl = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyPostLoginRedirectUrlKey, "Die Hager-Energy-Weiterleitungs-URL nach dem Login ist nicht konfiguriert.", cancellationToken),
            Scope = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyScopeKey, "Der Hager-Energy-OAuth-Scope ist nicht konfiguriert.", cancellationToken),
            OAuthState = await GetOptionalSettingAsync(ControllerSettingDefaults.HagerEnergyOAuthStateKey, cancellationToken),
            ApiKey = await GetOptionalSettingAsync(ControllerSettingDefaults.HagerEnergyApiKeyKey, cancellationToken),
            ClientId = await GetOptionalSettingAsync(ControllerSettingDefaults.HagerEnergyClientIdKey, cancellationToken),
            ClientSecret = await GetOptionalSettingAsync(ControllerSettingDefaults.HagerEnergyClientSecretKey, cancellationToken),
            RefreshToken = await GetOptionalSettingAsync(ControllerSettingDefaults.HagerEnergyRefreshTokenKey, cancellationToken),
            AccessToken = await GetOptionalSettingAsync(ControllerSettingDefaults.HagerEnergyAccessTokenKey, cancellationToken),
            InstallationId = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyInstallationIdKey, "Die Hager-Energy-Installation-ID ist nicht konfiguriert.", cancellationToken),
            GridImportJsonPath = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyGridImportJsonPathKey, "Der JSON-Pfad fuer Hager-Energy-Netzbezug ist nicht konfiguriert.", cancellationToken),
            PvProductionJsonPath = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyPvProductionJsonPathKey, "Der JSON-Pfad fuer Hager-Energy-PV-Leistung ist nicht konfiguriert.", cancellationToken),
            BatterySocJsonPath = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyBatterySocJsonPathKey, "Der JSON-Pfad fuer Hager-Energy-SoC ist nicht konfiguriert.", cancellationToken)
        };
    }

    private async Task<string> GetRequiredSettingAsync(string key, string missingMessage, CancellationToken cancellationToken)
    {
        var value = await GetOptionalSettingAsync(key, cancellationToken);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(missingMessage);
        }

        return value;
    }

    private async Task<string?> GetOptionalSettingAsync(string key, CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(key, cancellationToken);

        return setting is null || !setting.IsConfigured ? null : setting.Value;
    }
}
