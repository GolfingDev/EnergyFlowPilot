using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Reads Victron MQTT settings from persisted controller settings.
/// </summary>
public sealed class DatabaseVictronMqttSettingsProvider
{
    private readonly IControllerSettingStore controllerSettingStore;

    public DatabaseVictronMqttSettingsProvider(IControllerSettingStore controllerSettingStore)
    {
        this.controllerSettingStore = controllerSettingStore;
    }

    public async Task<VictronMqttSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return new VictronMqttSettings
        {
            Host = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronHostKey, "Der Victron-MQTT-Host ist nicht konfiguriert.", cancellationToken),
            Port = await GetRequiredIntegerSettingAsync(ControllerSettingDefaults.VictronPortKey, "Der Victron-MQTT-Port ist nicht konfiguriert.", "Der Victron-MQTT-Port muss als ganze Zahl konfiguriert sein.", cancellationToken),
            PortalId = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronPortalIdKey, "Die Victron-Portal-ID ist nicht konfiguriert.", cancellationToken),
            KeepAliveSeconds = await GetRequiredIntegerSettingAsync(ControllerSettingDefaults.VictronKeepAliveSecondsKey, "Der Victron-MQTT-KeepAlive ist nicht konfiguriert.", "Der Victron-MQTT-KeepAlive muss als ganze Zahl konfiguriert sein.", cancellationToken),
            StaleAfterSeconds = await GetRequiredIntegerSettingAsync(ControllerSettingDefaults.VictronStaleAfterSecondsKey, "Die Victron-Stale-Grenze ist nicht konfiguriert.", "Die Victron-Stale-Grenze muss als ganze Zahl konfiguriert sein.", cancellationToken),
            DryRun = await GetRequiredBooleanSettingAsync(ControllerSettingDefaults.VictronDryRunKey, "Die Victron-DryRun-Einstellung ist nicht konfiguriert.", "Die Victron-DryRun-Einstellung muss true oder false sein.", cancellationToken),
            GridPowerTopicTemplate = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronTopicGridPowerKey, "Das Victron-Topic fuer Netzleistung ist nicht konfiguriert.", cancellationToken),
            BatterySocTopicTemplate = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronTopicBatterySocKey, "Das Victron-Topic fuer Akku-SoC ist nicht konfiguriert.", cancellationToken),
            BatteryPowerTopicTemplate = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronTopicBatteryPowerKey, "Das Victron-Topic fuer Akku-Leistung ist nicht konfiguriert.", cancellationToken),
            HouseConsumptionTopicTemplate = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronTopicHouseConsumptionKey, "Das Victron-Topic fuer Hausverbrauch ist nicht konfiguriert.", cancellationToken),
            ChargeDischargeSetpointTopic = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronWriteTopicChargeDischargeSetpointKey, "Das Victron-Write-Topic fuer den Lade-/Entlade-Setpoint ist nicht konfiguriert.", cancellationToken),
            DisableChargeTopic = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronWriteTopicDisableChargeKey, "Das Victron-Write-Topic fuer DisableCharge ist nicht konfiguriert.", cancellationToken),
            DisableFeedInTopic = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronWriteTopicDisableFeedInKey, "Das Victron-Write-Topic fuer DisableFeedIn ist nicht konfiguriert.", cancellationToken),
            BatteryIdleThresholdWatts = await GetRequiredIntegerSettingAsync(ControllerSettingDefaults.VictronBatteryIdleThresholdWattsKey, "Die Victron-Batterie-Stillstandsschwelle ist nicht konfiguriert.", "Die Victron-Batterie-Stillstandsschwelle muss als ganze Watt-Zahl konfiguriert sein.", cancellationToken)
        };
    }

    private async Task<string> GetRequiredStringSettingAsync(string key, string missingMessage, CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(key, cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            throw new InvalidOperationException(missingMessage);
        }

        return setting.Value!;
    }

    private async Task<int> GetRequiredIntegerSettingAsync(string key, string missingMessage, string invalidMessage, CancellationToken cancellationToken)
    {
        var value = await GetRequiredStringSettingAsync(key, missingMessage, cancellationToken);

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
        {
            throw new InvalidOperationException(invalidMessage);
        }

        return parsedValue;
    }

    private async Task<bool> GetRequiredBooleanSettingAsync(string key, string missingMessage, string invalidMessage, CancellationToken cancellationToken)
    {
        var value = await GetRequiredStringSettingAsync(key, missingMessage, cancellationToken);

        if (!bool.TryParse(value, out var parsedValue))
        {
            throw new InvalidOperationException(invalidMessage);
        }

        return parsedValue;
    }
}
