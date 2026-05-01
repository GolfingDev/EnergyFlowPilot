using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Handles frontend-editable controller settings without exposing persistence details to the API layer.
/// </summary>
public interface IControllerSettingsService
{
    /// <summary>
    /// Gets all persisted settings for frontend read scenarios.
    /// </summary>
    Task<IReadOnlyList<ControllerSetting>> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a known setting while preserving the configured sensitivity from the default catalog.
    /// </summary>
    Task<ControllerSetting> UpdateSettingAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a compact status snapshot based on persisted settings.
    /// </summary>
    Task<ControllerStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
}
