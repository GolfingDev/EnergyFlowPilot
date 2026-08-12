using TibberVictronController.Business.Models;
using TibberVictronController.Dal.HagerEnergy;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Periodically polls the E3/DC (Hager Energy) Cloud API and stores the result in the
/// in-memory snapshot store for supplementary dashboard display.
/// Victron remains the lead telemetry source for all decisions.
/// </summary>
public sealed class HagerEnergyPollingBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly HagerEnergyTelemetrySnapshotStore snapshotStore;
    private readonly ILogger<HagerEnergyPollingBackgroundService> logger;

    public HagerEnergyPollingBackgroundService(
        IServiceScopeFactory scopeFactory,
        HagerEnergyTelemetrySnapshotStore snapshotStore,
        ILogger<HagerEnergyPollingBackgroundService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.snapshotStore = snapshotStore;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken);
            var intervalSeconds = await GetPollingIntervalSecondsAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var apiClient = scope.ServiceProvider.GetRequiredService<HagerEnergyApiClient>();
            var settings = await scope.ServiceProvider.GetRequiredService<DatabaseHagerEnergySettingsProvider>().GetSettingsAsync(cancellationToken);

            if (!settings.IsConfigured)
            {
                return;
            }

            var values = await apiClient.GetCurrentValuesAsync(cancellationToken);
            snapshotStore.Update(values);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "E3/DC-Telemetrie-Abruf fehlgeschlagen.");
        }
    }

    private async Task<int> GetPollingIntervalSecondsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var settingStore = scope.ServiceProvider.GetRequiredService<Business.Abstractions.IControllerSettingStore>();
            var setting = await settingStore.GetSettingAsync(ControllerSettingDefaults.HagerEnergyPollingIntervalSecondsKey, cancellationToken);

            if (setting is not null && setting.IsConfigured &&
                int.TryParse(setting.Value, out var seconds) && seconds >= 10)
            {
                return seconds;
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "E3/DC-Polling-Intervall konnte nicht gelesen werden, verwende Standard.");
        }

        return 60;
    }
}
