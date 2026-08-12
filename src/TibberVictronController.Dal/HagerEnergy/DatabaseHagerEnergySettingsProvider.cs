using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.HagerEnergy;

/// <summary>
/// Reads E3/DC (Hager Energy) API settings from persisted controller settings.
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
            ApiBaseUrl = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyApiBaseUrlKey, "Die E3/DC-API-Basis-URL ist nicht konfiguriert.", cancellationToken),
            TokenEndpoint = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyTokenEndpointKey, "Der E3/DC-Token-Endpunkt ist nicht konfiguriert.", cancellationToken),
            ClientId = await GetOptionalSettingAsync(ControllerSettingDefaults.HagerEnergyClientIdKey, cancellationToken),
            ClientSecret = await GetOptionalSettingAsync(ControllerSettingDefaults.HagerEnergyClientSecretKey, cancellationToken),
            InstallationId = await GetOptionalSettingAsync(ControllerSettingDefaults.HagerEnergyInstallationIdKey, cancellationToken),
            GridImportJsonPath = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyGridImportJsonPathKey, "Der JSON-Pfad fuer E3/DC-Netzbezug ist nicht konfiguriert.", cancellationToken),
            PvProductionJsonPath = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyPvProductionJsonPathKey, "Der JSON-Pfad fuer E3/DC-PV-Leistung ist nicht konfiguriert.", cancellationToken),
            BatterySocJsonPath = await GetRequiredSettingAsync(ControllerSettingDefaults.HagerEnergyBatterySocJsonPathKey, "Der JSON-Pfad fuer E3/DC-SoC ist nicht konfiguriert.", cancellationToken)
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
