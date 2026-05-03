using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Runs the realtime decision calculation on a fixed interval in shadow mode until hardware control is added.
/// </summary>
public sealed class DecisionExecutionBackgroundService : BackgroundService
{
    private const int DefaultIntervalSeconds = 60;
    private const int MinimumIntervalSeconds = 5;

    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<DecisionExecutionBackgroundService> logger;
    private readonly DecisionWorkerRuntimeStatus runtimeStatus;

    public DecisionExecutionBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<DecisionExecutionBackgroundService> logger,
        DecisionWorkerRuntimeStatus runtimeStatus)
    {
        this.serviceProvider = serviceProvider;
        this.logger = logger;
        this.runtimeStatus = runtimeStatus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        runtimeStatus.MarkStarting();

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(DefaultIntervalSeconds);

            try
            {
                delay = await ExecuteSingleCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await HandleCycleFailureAsync(exception, stoppingToken);
            }

            await Task.Delay(delay, stoppingToken);
        }

        runtimeStatus.MarkStopped();
    }

    public async Task<TimeSpan> ExecuteSingleCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var utcClock = scope.ServiceProvider.GetRequiredService<IUtcClock>();
        var currentBatteryDecisionService = scope.ServiceProvider.GetRequiredService<ICurrentBatteryDecisionService>();
        var controllerSettingStore = scope.ServiceProvider.GetRequiredService<IControllerSettingStore>();

        var interval = await GetIntervalAsync(controllerSettingStore, cancellationToken);
        var isShadowMode = await GetShadowModeAsync(controllerSettingStore, cancellationToken);
        var decisionResult = await currentBatteryDecisionService.CalculateCurrentDecisionAsync(cancellationToken);

        logger.LogInformation(
            "Decision-Worker-Zyklus abgeschlossen. ShadowMode={ShadowMode}, ZeitpunktUtc={TimestampUtc}, Zustand={DecisionState}, LeistungW={TargetPowerWatts}",
            isShadowMode,
            utcClock.UtcNow,
            decisionResult.Decision.Instruction.DecisionState,
            decisionResult.Decision.TargetPowerWatts);

        runtimeStatus.MarkSuccessful(utcClock.UtcNow);

        return interval;
    }

    public async Task HandleCycleFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var utcClock = scope.ServiceProvider.GetRequiredService<IUtcClock>();

        runtimeStatus.MarkFailed(exception.Message, utcClock.UtcNow);
        logger.LogError(exception, "Der Decision-Worker ist im Zyklus fehlgeschlagen.");
        await TrySaveFailureEventAsync(exception, utcClock.UtcNow, cancellationToken);
        await TrySendFailureNotificationAsync(exception, cancellationToken);
    }

    private async Task<TimeSpan> GetIntervalAsync(
        IControllerSettingStore controllerSettingStore,
        CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.DecisionWorkerIntervalSecondsKey,
            cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            return TimeSpan.FromSeconds(DefaultIntervalSeconds);
        }

        if (!int.TryParse(setting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalSeconds))
        {
            logger.LogWarning(
                "Decision-Worker-Intervall konnte nicht gelesen werden. Key={SettingKey}, Wert={SettingValue}. Fallback={FallbackSeconds}s",
                ControllerSettingDefaults.DecisionWorkerIntervalSecondsKey,
                setting.Value,
                DefaultIntervalSeconds);
            return TimeSpan.FromSeconds(DefaultIntervalSeconds);
        }

        return TimeSpan.FromSeconds(Math.Max(MinimumIntervalSeconds, intervalSeconds));
    }

    private async Task<bool> GetShadowModeAsync(
        IControllerSettingStore controllerSettingStore,
        CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.VictronDryRunKey,
            cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            return true;
        }

        return bool.TryParse(setting.Value, out var isDryRun) ? isDryRun : true;
    }

    private async Task TrySaveFailureEventAsync(Exception exception, DateTimeOffset failedAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var operationalEventRepository = scope.ServiceProvider.GetRequiredService<IOperationalEventRepository>();
            var operationalEvent = new OperationalEvent(
                Guid.NewGuid(),
                failedAtUtc,
                category: "DecisionWorker",
                severity: "Error",
                message: "Der Decision-Worker ist fehlgeschlagen.",
                details: exception.ToString());

            await operationalEventRepository.SaveEventAsync(operationalEvent, cancellationToken);
        }
        catch (Exception persistenceException)
        {
            logger.LogError(persistenceException, "Der Fehler des Decision-Workers konnte nicht als Operational Event gespeichert werden.");
        }
    }

    private async Task TrySendFailureNotificationAsync(Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var workerFailureNotifier = scope.ServiceProvider.GetRequiredService<IWorkerFailureNotifier>();
            await workerFailureNotifier.NotifyAsync(exception, cancellationToken);
        }
        catch (Exception notificationException)
        {
            logger.LogError(notificationException, "Die Fehlermail des Decision-Workers konnte nicht versendet werden.");
        }
    }
}
