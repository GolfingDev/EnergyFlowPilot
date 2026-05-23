using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Battery;

/// <summary>
/// Selects the configured source per live telemetry value.
/// </summary>
public sealed class TelemetrySourceSelector
{
    public const string VictronMqttProvider = "victronMqtt";
    public const string HagerEnergyApiProvider = "hagerEnergyApi";

    private readonly IControllerSettingStore controllerSettingStore;

    public TelemetrySourceSelector(IControllerSettingStore controllerSettingStore)
    {
        this.controllerSettingStore = controllerSettingStore;
    }

    public Task<string> GetGridImportSourceAsync(CancellationToken cancellationToken = default)
    {
        return GetConfiguredSourceAsync(ControllerSettingDefaults.TelemetryGridImportSourceKey, cancellationToken);
    }

    public Task<string> GetPvProductionSourceAsync(CancellationToken cancellationToken = default)
    {
        return GetConfiguredSourceAsync(ControllerSettingDefaults.TelemetryPvProductionSourceKey, cancellationToken);
    }

    public Task<string> GetBatterySocSourceAsync(CancellationToken cancellationToken = default)
    {
        return GetConfiguredSourceAsync(ControllerSettingDefaults.TelemetryBatterySocSourceKey, cancellationToken);
    }

    private async Task<string> GetConfiguredSourceAsync(string settingKey, CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(settingKey, cancellationToken);

        return setting is null || !setting.IsConfigured ? VictronMqttProvider : setting.Value!;
    }
}
