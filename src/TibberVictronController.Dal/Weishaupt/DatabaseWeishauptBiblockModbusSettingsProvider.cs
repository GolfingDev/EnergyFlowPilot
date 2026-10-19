using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Weishaupt;

public sealed class DatabaseWeishauptBiblockModbusSettingsProvider
{
    private readonly IControllerSettingStore controllerSettingStore;

    public DatabaseWeishauptBiblockModbusSettingsProvider(IControllerSettingStore controllerSettingStore)
    {
        this.controllerSettingStore = controllerSettingStore;
    }

    public async Task<WeishauptBiblockModbusSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var enabled = bool.TryParse(await GetValueAsync(ControllerSettingDefaults.WeishauptBiblockModbusEnabledKey, cancellationToken), out var enabledValue) && enabledValue;
        var interval = await GetIntAsync(ControllerSettingDefaults.WeishauptBiblockModbusPollingIntervalSecondsKey, 300, cancellationToken);

        return new WeishauptBiblockModbusSettings
        {
            IsEnabled = enabled,
            Host = await GetValueAsync(ControllerSettingDefaults.WeishauptBiblockModbusHostKey, cancellationToken) ?? string.Empty,
            Port = await GetIntAsync(ControllerSettingDefaults.WeishauptBiblockModbusPortKey, 502, cancellationToken),
            SlaveId = (byte)Math.Clamp(await GetIntAsync(ControllerSettingDefaults.WeishauptBiblockModbusSlaveIdKey, 1, cancellationToken), 0, 247),
            PollingIntervalSeconds = Math.Max(WeishauptBiblockModbusSettings.MinimumPollingIntervalSeconds, interval),
            SampleRetentionDays = await GetIntAsync(ControllerSettingDefaults.WeishauptBiblockModbusSampleRetentionDaysKey, 400, cancellationToken)
        };
    }

    private async Task<int> GetIntAsync(string key, int fallback, CancellationToken cancellationToken)
    {
        var value = await GetValueAsync(key, cancellationToken);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(key, cancellationToken);
        return setting is { IsConfigured: true } ? setting.Value : null;
    }
}
