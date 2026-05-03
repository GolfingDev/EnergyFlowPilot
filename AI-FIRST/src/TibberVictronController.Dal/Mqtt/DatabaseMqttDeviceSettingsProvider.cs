using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Mqtt;

/// <summary>
/// Reads generic MQTT device settings from persisted controller settings.
/// </summary>
public sealed class DatabaseMqttDeviceSettingsProvider
{
    private readonly IControllerSettingStore controllerSettingStore;

    public DatabaseMqttDeviceSettingsProvider(IControllerSettingStore controllerSettingStore)
    {
        this.controllerSettingStore = controllerSettingStore;
    }

    public async Task<MqttDeviceSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return new MqttDeviceSettings
        {
            Host = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronHostKey, "Der MQTT-Host des Geräts ist nicht konfiguriert.", cancellationToken),
            Port = await GetRequiredIntegerSettingAsync(ControllerSettingDefaults.VictronPortKey, "Der MQTT-Port des Geräts ist nicht konfiguriert.", "Der MQTT-Port des Geräts muss als ganze Zahl konfiguriert sein.", cancellationToken),
            DeviceId = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronPortalIdKey, "Die Geräte-ID für MQTT ist nicht konfiguriert.", cancellationToken),
            KeepAliveSeconds = await GetRequiredIntegerSettingAsync(ControllerSettingDefaults.VictronKeepAliveSecondsKey, "Das MQTT-KeepAlive ist nicht konfiguriert.", "Das MQTT-KeepAlive muss als ganze Zahl konfiguriert sein.", cancellationToken),
            StaleAfterSeconds = await GetRequiredIntegerSettingAsync(ControllerSettingDefaults.VictronStaleAfterSecondsKey, "Die MQTT-Stale-Grenze ist nicht konfiguriert.", "Die MQTT-Stale-Grenze muss als ganze Zahl konfiguriert sein.", cancellationToken),
            DryRun = await GetRequiredBooleanSettingAsync(ControllerSettingDefaults.VictronDryRunKey, "Die MQTT-DryRun-Einstellung ist nicht konfiguriert.", "Die MQTT-DryRun-Einstellung muss true oder false sein.", cancellationToken),
            GridPowerTopicTemplate = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronTopicGridPowerKey, "Das MQTT-Topic für Netzleistung ist nicht konfiguriert.", cancellationToken),
            BatterySocTopicTemplate = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronTopicBatterySocKey, "Das MQTT-Topic für Akku-SoC ist nicht konfiguriert.", cancellationToken),
            BatteryPowerTopicTemplate = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronTopicBatteryPowerKey, "Das MQTT-Topic für Akku-Leistung ist nicht konfiguriert.", cancellationToken),
            HouseConsumptionTopicTemplate = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronTopicHouseConsumptionKey, "Das MQTT-Topic für Hausverbrauch ist nicht konfiguriert.", cancellationToken),
            ChargeDischargeSetpointTopic = await GetRequiredStringSettingAsync(ControllerSettingDefaults.VictronWriteTopicChargeDischargeSetpointKey, "Das MQTT-Write-Topic für den Lade-/Entlade-Setpoint ist nicht konfiguriert.", cancellationToken)
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
