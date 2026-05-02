using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Calculates the current direct Decision Engine result for the live control path.
/// </summary>
public interface ICurrentBatteryDecisionService
{
    /// <summary>
    /// Calculates the current direct decision, including validation of live inputs and structured reasons.
    /// </summary>
    Task<CurrentBatteryDecisionResult> CalculateCurrentDecisionAsync(CancellationToken cancellationToken = default);
}
