using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Services;

/// <summary>
/// Provides the application use case for reading and updating persisted controller settings.
/// </summary>
public sealed class ControllerSettingsService : IControllerSettingsService
{
    private static readonly SemaphoreSlim UpdateGate = new(1, 1);
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
        await UpdateGate.WaitAsync(cancellationToken);
        try { return await UpdateCoreAsync(key, value, cancellationToken); }
        finally { UpdateGate.Release(); }
    }

    private async Task<ControllerSetting> UpdateCoreAsync(string key, string? value, CancellationToken cancellationToken)
    {
        var definition = GetRequiredDefinition(key);
        if ((definition.Key.StartsWith("victron.", StringComparison.Ordinal) && definition.Key != ControllerSettingDefaults.VictronDryRunKey) ||
            definition.Key.StartsWith("e3dc.rscp.", StringComparison.Ordinal))
        {
            foreach (var flag in new[] { ControllerSettingDefaults.JointBatteryExecutionEnabledKey, ControllerSettingDefaults.JointBatteryRecoveryPendingKey })
                if (bool.TryParse((await controllerSettingStore.GetSettingAsync(flag, cancellationToken))?.Value, out var active) && active)
                    throw new ArgumentException("Zuerst die gemeinsame Steuerung deaktivieren und ihre Ruecknahme abwarten, bevor Geraeteadressen oder Hardwarezugang geaendert werden.");
        }
        if (definition.Key == ControllerSettingDefaults.JointBatteryRecoveryPendingKey)
            throw new ArgumentException("Der Wiederherstellungsstatus wird ausschliesslich von der gemeinsamen Steuerung gesetzt.", nameof(key));
        if (definition.Key.StartsWith(ControllerSettingDefaults.JointManualChargePrefix, StringComparison.Ordinal))
            throw new ArgumentException("Die manuelle Ladung beider Akkus wird ueber das Dashboard gestartet und beendet.", nameof(key));
        if (definition.Key == ControllerSettingDefaults.ElectricityPriceProviderChangedAtKey)
            throw new ArgumentException("Der Anbieterwechsel-Zeitpunkt wird automatisch gesetzt.", nameof(key));
        var normalizedValue = NormalizeValue(definition, value);
        ControllerSettingValueValidation.Validate(definition.Key, normalizedValue);
        if (definition.Key == ControllerSettingDefaults.ElectricityPriceProviderKey && normalizedValue is not ("tibber" or "octopusGermany"))
            throw new ArgumentException("Die Strompreisquelle muss tibber oder octopusGermany sein.", nameof(value));
        if (definition.Key == ControllerSettingDefaults.WeishauptBiblockModbusPollingIntervalSecondsKey &&
            (!int.TryParse(normalizedValue, out var pollingSeconds) || pollingSeconds is < 60 or > 3600))
            throw new ArgumentException("Das Biblock-Abfrageintervall muss zwischen 60 und 3600 Sekunden liegen.", nameof(value));
        var updatedSetting = new ControllerSetting(
            definition.Key,
            normalizedValue,
            definition.Sensitivity,
            utcClock.UtcNow);

        if (definition.Key == ControllerSettingDefaults.ElectricityPriceProviderKey)
        {
            var previous = (await controllerSettingStore.GetSettingAsync(definition.Key, cancellationToken))?.Value ?? "tibber";
            if (previous != normalizedValue)
                await controllerSettingStore.SaveSettingAsync(new ControllerSetting(
                    ControllerSettingDefaults.ElectricityPriceProviderChangedAtKey, utcClock.UtcNow.ToString("O"),
                    ControllerSettingSensitivity.Normal, utcClock.UtcNow), cancellationToken);
        }
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
            utcClock.UtcNow)
        {
            ElectricityPriceProvider = persistedSettings.FirstOrDefault(s =>
                s.Key == ControllerSettingDefaults.ElectricityPriceProviderKey)?.Value ?? "tibber"
        };
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
