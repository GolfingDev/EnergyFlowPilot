using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Mqtt;

namespace TibberVictronController.Api.Dashboard;

public interface IDashboardLiveUpdatePublisher
{
    Task PublishDecisionAsync(CurrentBatteryDecisionResult decisionResult, CancellationToken cancellationToken = default);

    Task PublishTelemetryAsync(MqttTelemetrySnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>Sends the live view of a freshly stored sample, the same values the dashboard summary shows.</summary>
    Task PublishLiveStateAsync(LiveConsumptionSample sample, CancellationToken cancellationToken = default);

    /// <summary>Tells the dashboards that the data of one area changed, so they reload just that area.</summary>
    Task PublishChangedAsync(string area, CancellationToken cancellationToken = default);
}
