namespace TibberVictronController.Business.Models;

/// <summary>
/// Defines one known controller setting and its default persistence value.
/// </summary>
public sealed record ControllerSettingDefinition
{
    /// <summary>
    /// Validates default metadata before it can be used for database seeding.
    /// </summary>
    public ControllerSettingDefinition(
        string key,
        string? defaultValue,
        ControllerSettingSensitivity sensitivity)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Der Einstellungsschluessel muss angegeben werden.", nameof(key));
        }

        if (sensitivity == ControllerSettingSensitivity.Normal && string.IsNullOrWhiteSpace(defaultValue))
        {
            throw new ArgumentException("Eine normale Einstellung braucht einen Default-Wert.", nameof(defaultValue));
        }

        Key = key;
        DefaultValue = defaultValue;
        Sensitivity = sensitivity;
    }

    /// <summary>
    /// Gets the stable setting key used by persistence and application services.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the default value. Sensitive access data may use null to mean not configured.
    /// </summary>
    public string? DefaultValue { get; }

    /// <summary>
    /// Gets whether this setting contains sensitive access data.
    /// </summary>
    public ControllerSettingSensitivity Sensitivity { get; }

    /// <summary>
    /// Creates a persisted setting instance from this default definition.
    /// </summary>
    public ControllerSetting CreateSetting(DateTimeOffset updatedAtUtc)
    {
        return new ControllerSetting(Key, DefaultValue, Sensitivity, updatedAtUtc);
    }
}
