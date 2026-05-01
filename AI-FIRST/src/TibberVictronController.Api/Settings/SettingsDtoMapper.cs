using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Settings;

/// <summary>
/// Maps controller setting domain models to frontend-safe DTOs.
/// </summary>
public static class SettingsDtoMapper
{
    /// <summary>
    /// Maps a setting collection and hides sensitive values from frontend read contracts.
    /// </summary>
    public static ControllerSettingsResponseDto MapSettings(IReadOnlyList<ControllerSetting> settings)
    {
        var settingDtos = settings
            .Select(MapSetting)
            .ToArray();

        return new ControllerSettingsResponseDto(settingDtos);
    }

    /// <summary>
    /// Maps one setting while keeping the real value hidden for sensitive access data.
    /// </summary>
    public static ControllerSettingResponseDto MapSetting(ControllerSetting setting)
    {
        return new ControllerSettingResponseDto(
            setting.Key,
            setting.GetFrontendReadableValue(),
            setting.Sensitivity == ControllerSettingSensitivity.Sensitive,
            setting.IsConfigured,
            setting.UpdatedAtUtc);
    }

    /// <summary>
    /// Maps a controller status snapshot to the API response shape.
    /// </summary>
    public static ControllerStatusResponseDto MapStatus(ControllerStatusSnapshot status)
    {
        return new ControllerStatusResponseDto(
            status.Status,
            status.KnownSettingsCount,
            status.PersistedSettingsCount,
            status.ConfiguredSensitiveSettingsCount,
            status.GeneratedAtUtc);
    }
}
