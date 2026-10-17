using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Homematic;

/// <summary>
/// Reads Homematic XML-API settings from persisted controller settings.
/// </summary>
public sealed class DatabaseHomematicSettingsProvider
{
    private const int DefaultPollingIntervalSeconds = 300;
    private const int DefaultSampleRetentionDays = 400;

    private readonly IControllerSettingStore controllerSettingStore;

    public DatabaseHomematicSettingsProvider(IControllerSettingStore controllerSettingStore)
    {
        this.controllerSettingStore = controllerSettingStore;
    }

    public async Task<HomematicSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var enabledValue = await GetOptionalSettingAsync(ControllerSettingDefaults.HomematicEnabledKey, cancellationToken);
        var pollingIntervalSeconds = await GetPositiveIntegerAsync(
            ControllerSettingDefaults.HomematicPollingIntervalSecondsKey,
            DefaultPollingIntervalSeconds,
            cancellationToken);

        return new HomematicSettings
        {
            IsEnabled = bool.TryParse(enabledValue, out var isEnabled) && isEnabled,
            BaseUrl = await GetOptionalSettingAsync(ControllerSettingDefaults.HomematicBaseUrlKey, cancellationToken) ?? string.Empty,
            ApiToken = await GetOptionalSettingAsync(ControllerSettingDefaults.HomematicApiTokenKey, cancellationToken),
            // Protect the CCU from tight polling loops caused by typos in the settings UI.
            PollingIntervalSeconds = Math.Max(HomematicSettings.MinimumPollingIntervalSeconds, pollingIntervalSeconds),
            SampleRetentionDays = await GetPositiveIntegerAsync(
                ControllerSettingDefaults.HomematicSampleRetentionDaysKey,
                DefaultSampleRetentionDays,
                cancellationToken)
        };
    }

    private async Task<int> GetPositiveIntegerAsync(string key, int defaultValue, CancellationToken cancellationToken)
    {
        var value = await GetOptionalSettingAsync(key, cancellationToken);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }

    private async Task<string?> GetOptionalSettingAsync(string key, CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(key, cancellationToken);

        return setting is null || !setting.IsConfigured ? null : setting.Value;
    }
}
