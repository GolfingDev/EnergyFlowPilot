using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Provides the current battery state for Decision Engine forecast and direct decision calculations.
/// </summary>
public interface IBatteryStateProvider
{
    /// <summary>
    /// Gets the latest known battery state.
    /// </summary>
    Task<BatteryState> GetCurrentBatteryStateAsync(CancellationToken cancellationToken = default);
}
