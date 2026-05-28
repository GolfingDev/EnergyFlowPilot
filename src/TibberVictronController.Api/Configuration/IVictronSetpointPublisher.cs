using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Publishes realtime battery control setpoints to Victron.
/// </summary>
public interface IVictronSetpointPublisher
{
    Task PublishAsync(CurrentBatteryDecisionResult decisionResult, CancellationToken cancellationToken = default);
}
