using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Prepares stored Homematic climate samples for the heating view.
/// </summary>
public interface IHomematicClimateHistoryService
{
    Task<HomematicClimateHistory> GetHistoryAsync(HomematicHistoryRange range, CancellationToken cancellationToken = default);
}
