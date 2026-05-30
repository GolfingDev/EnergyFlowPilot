using Microsoft.AspNetCore.SignalR;
using TibberVictronController.Api.Decision;
using TibberVictronController.Dal.Mqtt;

namespace TibberVictronController.Api.Dashboard;

public sealed class SignalRDashboardLiveUpdatePublisher : IDashboardLiveUpdatePublisher
{
    public const string DecisionUpdatedEventName = "dashboardDecisionUpdated";
    public const string TelemetryUpdatedEventName = "dashboardTelemetryUpdated";

    private readonly IHubContext<DashboardHub> hubContext;

    public SignalRDashboardLiveUpdatePublisher(IHubContext<DashboardHub> hubContext)
    {
        this.hubContext = hubContext;
    }

    public async Task PublishDecisionAsync(
        Business.Models.CurrentBatteryDecisionResult decisionResult,
        CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync(
            DecisionUpdatedEventName,
            CurrentDecisionDtoMapper.Map(decisionResult),
            cancellationToken);
    }

    public async Task PublishTelemetryAsync(
        MqttTelemetrySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var telemetry = MapTelemetry(snapshot);
        if (telemetry is null)
        {
            return;
        }

        await hubContext.Clients.All.SendAsync(
            TelemetryUpdatedEventName,
            telemetry,
            cancellationToken);
    }

    private static DashboardTelemetryUpdateDto? MapTelemetry(MqttTelemetrySnapshot snapshot)
    {
        if (snapshot.GridPowerWatts is null || snapshot.GridPowerMeasuredAtUtc is null)
        {
            return null;
        }

        var effectiveHouseConsumptionWatts = snapshot.HouseConsumptionWatts is null or 0m
            ? snapshot.GridPowerWatts
            : snapshot.HouseConsumptionWatts;
        var effectiveHouseConsumptionMeasuredAtUtc = snapshot.HouseConsumptionWatts is null or 0m
            ? snapshot.GridPowerMeasuredAtUtc
            : snapshot.HouseConsumptionMeasuredAtUtc;

        if (effectiveHouseConsumptionWatts is null || effectiveHouseConsumptionMeasuredAtUtc is null)
        {
            return null;
        }

        var measuredAtUtc = snapshot.GridPowerMeasuredAtUtc.Value <= effectiveHouseConsumptionMeasuredAtUtc.Value
            ? snapshot.GridPowerMeasuredAtUtc.Value
            : effectiveHouseConsumptionMeasuredAtUtc.Value;

        return new DashboardTelemetryUpdateDto(
            DecimalToInt(snapshot.GridPowerWatts.Value),
            effectiveHouseConsumptionWatts.Value < 0m
                ? DecimalToInt(Math.Abs(effectiveHouseConsumptionWatts.Value))
                : 0,
            snapshot.BatteryPowerWatts is null ? null : DecimalToInt(snapshot.BatteryPowerWatts.Value),
            snapshot.BatterySocPercent,
            measuredAtUtc);
    }

    private static int DecimalToInt(decimal value)
    {
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
