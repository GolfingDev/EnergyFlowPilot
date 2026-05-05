using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Stores frontend-editable runtime settings and access data in the configured persistence source.
/// </summary>
public interface IControllerSettingStore
{
    /// <summary>
    /// Gets all known controller settings for application services.
    /// </summary>
    Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets one controller setting by its stable key.
    /// </summary>
    Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a controller setting or access-data value.
    /// </summary>
    Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default);
}
