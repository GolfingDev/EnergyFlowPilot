using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Services;

/// <summary>
/// Provides the application use case for reading and updating persisted controller settings.
/// </summary>
public sealed class ControllerSettingsService : IControllerSettingsService
{
    private readonly IControllerSettingStore controllerSettingStore;
    private readonly IUtcClock utcClock;

    public ControllerSettingsService(
        IControllerSettingStore controllerSettingStore,
        IUtcClock utcClock)
    {
        this.controllerSettingStore = controllerSettingStore
            ?? throw new ArgumentNullException(nameof(controllerSettingStore), "Der Einstellungsspeicher darf nicht null sein.");
        this.utcClock = utcClock
            ?? throw new ArgumentNullException(nameof(utcClock), "Die UTC-Uhr darf nicht null sein.");
    }

    /// <summary>
    /// Returns all settings from persistence. Sensitive values remain protected by the domain model.
    /// </summary>
    public Task<IReadOnlyList<ControllerSetting>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return controllerSettingStore.GetAllSettingsAsync(cancellationToken);
    }

    /// <summary>
    /// Updates only known settings and always uses the sensitivity defined by the default catalog.
    /// </summary>
    public async Task<ControllerSetting> UpdateSettingAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        var definition = GetRequiredDefinition(key);
        var normalizedValue = NormalizeValue(definition, value);
        var updatedSetting = new ControllerSetting(
            definition.Key,
            normalizedValue,
            definition.Sensitivity,
            utcClock.UtcNow);

        await controllerSettingStore.SaveSettingAsync(updatedSetting, cancellationToken);

        return updatedSetting;
    }

    /// <summary>
    /// Builds a compact health snapshot from the default catalog and persisted settings.
    /// </summary>
    public async Task<ControllerStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var knownSettings = ControllerSettingDefaults.GetDefinitions();
        var persistedSettings = await controllerSettingStore.GetAllSettingsAsync(cancellationToken);
        var configuredSensitiveSettingsCount = persistedSettings.Count(setting =>
            setting.Sensitivity == ControllerSettingSensitivity.Sensitive &&
            setting.IsConfigured);

        return new ControllerStatusSnapshot(
            "Healthy",
            knownSettings.Count,
            persistedSettings.Count,
            configuredSensitiveSettingsCount,
            utcClock.UtcNow);
    }

    private static ControllerSettingDefinition GetRequiredDefinition(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Der Einstellungsschluessel muss angegeben werden.", nameof(key));
        }

        var definition = ControllerSettingDefaults
            .GetDefinitions()
            .SingleOrDefault(settingDefinition =>
                string.Equals(settingDefinition.Key, key, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
        {
            throw new KeyNotFoundException($"Die Einstellung '{key}' ist nicht bekannt.");
        }

        return definition;
    }

    private static string? NormalizeValue(ControllerSettingDefinition definition, string? value)
    {
        if (definition.Sensitivity == ControllerSettingSensitivity.Sensitive)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Die Einstellung '{definition.Key}' braucht einen Wert.", nameof(value));
        }

        return value.Trim();
    }
}
