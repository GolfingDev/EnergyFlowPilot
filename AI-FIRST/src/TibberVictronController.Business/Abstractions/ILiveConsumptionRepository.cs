using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Persists real measured live house consumption samples for later analysis and forecasting.
/// </summary>
public interface ILiveConsumptionRepository
{
    /// <summary>
    /// Saves one live house consumption sample.
    /// </summary>
    Task SaveSampleAsync(LiveConsumptionSample sample, CancellationToken cancellationToken = default);
}
