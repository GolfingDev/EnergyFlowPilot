using TibberVictronController.Business.Abstractions;
using TibberVictronController.Dal.Weishaupt;

namespace TibberVictronController.Api.Configuration;

/// <summary>Periodically collects WBB input registers using Modbus FC04 only.</summary>
public sealed class WeishauptBiblockModbusPollingBackgroundService : BackgroundService
{
    private static readonly TimeSpan RetentionCleanupInterval = TimeSpan.FromDays(1);
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IUtcClock utcClock;
    private readonly ILogger<WeishauptBiblockModbusPollingBackgroundService> logger;
    private DateTimeOffset? lastRetentionCleanupAtUtc;

    public WeishauptBiblockModbusPollingBackgroundService(
        IServiceScopeFactory scopeFactory,
        IUtcClock utcClock,
        ILogger<WeishauptBiblockModbusPollingBackgroundService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.utcClock = utcClock;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = await PollOnceAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        var interval = WeishauptBiblockModbusSettings.MinimumPollingIntervalSeconds;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var settings = await scope.ServiceProvider.GetRequiredService<DatabaseWeishauptBiblockModbusSettingsProvider>().GetSettingsAsync(cancellationToken);
            interval = settings.PollingIntervalSeconds;
            if (!settings.IsReadyForPolling)
            {
                return interval;
            }

            var nowUtc = utcClock.UtcNow;
            var source = scope.ServiceProvider.GetRequiredService<IWeishauptBiblockTelemetrySource>();
            var repository = scope.ServiceProvider.GetRequiredService<IWeishauptBiblockTelemetryRepository>();
            var sample = await source.GetCurrentSampleAsync(nowUtc, cancellationToken);
            await repository.SaveSampleAsync(sample, cancellationToken);

            if (lastRetentionCleanupAtUtc is null || nowUtc - lastRetentionCleanupAtUtc >= RetentionCleanupInterval)
            {
                var deleted = await repository.DeleteSamplesOlderThanAsync(nowUtc.AddDays(-settings.SampleRetentionDays), cancellationToken);
                lastRetentionCleanupAtUtc = nowUtc;
                if (deleted > 0)
                {
                    logger.LogInformation("{DeletedCount} alte Weishaupt-Biblock-Telemetriedatensaetze geloescht.", deleted);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Weishaupt-Biblock-Modbusdaten konnten nicht per FC04 abgerufen oder gespeichert werden. Naechster Versuch im naechsten Zyklus.");
        }

        return interval;
    }
}
