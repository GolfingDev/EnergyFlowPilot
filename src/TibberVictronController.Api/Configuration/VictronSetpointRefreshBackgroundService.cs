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

        foreach (var setpoint in snapshot.Setpoints)
        {
            await mqttControlClient.PublishValueAsync(setpoint.Topic, setpoint.Value, cancellationToken);
        }

        logger.LogDebug(
            "Victron-Setpoint refreshed. Count={SetpointCount}, ValidToUtc={ValidToUtc}",
            snapshot.Setpoints.Count,
            snapshot.ValidToUtc);
    }
}
