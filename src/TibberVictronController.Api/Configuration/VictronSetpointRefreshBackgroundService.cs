using TibberVictronController.Business.Abstractions;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Refreshes the last Victron setpoint at a short cadence required by External ESS control.
/// </summary>
public sealed class VictronSetpointRefreshBackgroundService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider serviceProvider;
    private readonly VictronSetpointRefreshState refreshState;
    private readonly IVictronMqttControlClient mqttControlClient;
    private readonly ILogger<VictronSetpointRefreshBackgroundService> logger;

    public VictronSetpointRefreshBackgroundService(
        IServiceProvider serviceProvider,
        VictronSetpointRefreshState refreshState,
        IVictronMqttControlClient mqttControlClient,
        ILogger<VictronSetpointRefreshBackgroundService> logger)
    {
        this.serviceProvider = serviceProvider;
        this.refreshState = refreshState;
        this.mqttControlClient = mqttControlClient;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Victron-Setpoint-Refresh ist fehlgeschlagen.");
            }

            await Task.Delay(RefreshInterval, stoppingToken);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var utcClock = scope.ServiceProvider.GetRequiredService<IUtcClock>();
        var settingsProvider = scope.ServiceProvider.GetRequiredService<DatabaseVictronMqttSettingsProvider>();
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);

        if (settings.DryRun || settings.ControlMode != VictronControlMode.ExternalEss)
        {
            return;
        }

        if (!refreshState.TryGet(utcClock.UtcNow, out var snapshot))
        {
            return;
        }

        var topics = VictronMqttTopicFactory.Create(settings);
        // The battery target ramps toward the decision; the setpoint is rebuilt with the current AC-out load every refresh,
        // so a wallbox or other L1 load that starts between two decisions does not drain the battery.
        var signedBatteryTargetWatts = MoveToward(
            snapshot.SignedBatteryTargetWatts,
            snapshot.DesiredSignedBatteryTargetWatts,
            MqttVictronSetpointPublisher.SetpointRampStepWatts);
        var setpoints = MqttVictronSetpointPublisher.CreateExternalEssSetpoints(topics, signedBatteryTargetWatts, mqttControlClient);
        var hub4Control = MqttVictronSetpointPublisher.CalculateHub4ControlFromSignedBatteryTarget(
            signedBatteryTargetWatts,
            settings.BatteryIdleThresholdWatts);
        await PublishValueIfChangedAsync(topics.DisableChargeTopic, topics.DisableChargeReadTopic, hub4Control.DisableCharge ? 1 : 0, cancellationToken);
        await PublishValueIfChangedAsync(topics.DisableFeedInTopic, topics.DisableFeedInReadTopic, hub4Control.DisableFeedIn ? 1 : 0, cancellationToken);

        foreach (var setpoint in setpoints)
        {
            await mqttControlClient.PublishValueAsync(setpoint.Topic, setpoint.Value, cancellationToken);
        }

        refreshState.Update(snapshot with
        {
            Setpoints = setpoints,
            SignedBatteryTargetWatts = signedBatteryTargetWatts
        });

        logger.LogDebug(
            "Victron-Setpoint refreshed. Count={SetpointCount}, ValidToUtc={ValidToUtc}",
            setpoints.Count,
            snapshot.ValidToUtc);
    }

    private static int MoveToward(int currentValue, int desiredValue, int maximumStepWatts)
    {
        var delta = desiredValue - currentValue;

        return Math.Abs(delta) <= maximumStepWatts
            ? desiredValue
            : currentValue + Math.Sign(delta) * maximumStepWatts;
    }

    private async Task PublishValueIfChangedAsync(
        string writeTopic,
        string readTopic,
        int desiredValue,
        CancellationToken cancellationToken)
    {
        if (mqttControlClient.TryGetLatestValue(readTopic, out var currentValue) &&
            currentValue == desiredValue)
        {
            return;
        }

        await mqttControlClient.PublishValueAsync(writeTopic, desiredValue, cancellationToken);
    }
}
