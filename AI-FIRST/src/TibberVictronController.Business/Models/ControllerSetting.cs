namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents one persisted runtime setting or access-data value.
/// </summary>
public sealed record ControllerSetting
{
    /// <summary>
    /// Validates setting metadata before it is stored or exposed through application services.
    /// </summary>
    public ControllerSetting(
        string key,
        string? value,
        ControllerSettingSensitivity sensitivity,
        DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Der Einstellungsschluessel muss angegeben werden.", nameof(key));
        }

        if (updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Aktualisierungszeitpunkt der Einstellung muss in UTC angegeben sein.", nameof(updatedAtUtc));
        }

        Key = key;
        Value = value;
        Sensitivity = sensitivity;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>
    /// Gets the stable setting key used by persistence and application services.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the stored value. Sensitive values must not be returned directly to frontend read contracts.
    /// </summary>
    public string? Value { get; }

    /// <summary>
    /// Gets whether the setting contains sensitive access data.
    /// </summary>
    public ControllerSettingSensitivity Sensitivity { get; }

    /// <summary>
    /// Gets when the setting was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>
    /// Gets whether a value is currently configured without revealing sensitive access data.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Value);

    /// <summary>
    /// Returns the value only when it is safe for frontend read scenarios.
    /// </summary>
    public string? GetFrontendReadableValue()
    {
        return Sensitivity == ControllerSettingSensitivity.Sensitive ? null : Value;
    }
}
