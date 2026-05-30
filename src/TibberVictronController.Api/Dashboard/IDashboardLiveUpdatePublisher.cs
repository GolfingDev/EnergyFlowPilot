using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Mqtt;

namespace TibberVictronController.Api.Dashboard;

public interface IDashboardLiveUpdatePublisher
{
    Task PublishDecisionAsync(CurrentBatteryDecisionResult decisionResult, CancellationToken cancellationToken = default);

    Task PublishTelemetryAsync(MqttTelemetrySnapshot snapshot, CancellationToken cancellationToken = default);
}
