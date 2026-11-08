namespace TibberVictronController.Api.Settings;

public sealed record ControllerSettingsResponseDto(IReadOnlyList<ControllerSettingResponseDto> Settings);

public sealed record ControllerSettingResponseDto(
    string Key,
    string? Value,
    bool IsSensitive,
    bool IsConfigured,
    DateTimeOffset UpdatedAtUtc);

public sealed record UpdateControllerSettingRequestDto(string? Value);

public sealed record ControllerStatusResponseDto(
    string Status,
    int KnownSettingsCount,
    int PersistedSettingsCount,
    int ConfiguredSensitiveSettingsCount,
    DateTimeOffset GeneratedAtUtc,
    string? VictronMqttStatus,
    string? VictronMqttLastError,
    DateTimeOffset? VictronMqttLastSuccessfulMessageAtUtc)
{
    public string ElectricityPriceProvider { get; init; } = "tibber";

    /// <summary>Start of the last completed decision cycle; lets the UI confirm that a saved setting was used.</summary>
    public DateTimeOffset? DecisionWorkerLastCompletedCycleStartedAtUtc { get; init; }

    /// <summary>Settings the last Victron MQTT error names; the page offers to enter them.</summary>
    public IReadOnlyList<string> VictronMqttMissingSettings { get; init; } = [];
}

public sealed record SettingsErrorDto(string Message);
