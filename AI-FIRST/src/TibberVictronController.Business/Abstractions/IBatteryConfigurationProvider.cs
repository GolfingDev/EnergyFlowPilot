using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Provides persisted battery configuration for Decision Engine calculations.
/// </summary>
public interface IBatteryConfigurationProvider
{
    /// <summary>
    /// Gets the current battery configuration from the configured persistence source.
    /// </summary>
    Task<BatteryConfiguration> GetBatteryConfigurationAsync(CancellationToken cancellationToken = default);
}
