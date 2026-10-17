using TibberVictronController.Business.Abstractions;
using TibberVictronController.Dal.Homematic;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Periodically reads Homematic room climate values and stores them for later heating curve analysis.
/// Strictly read-only towards the CCU and without influence on battery decisions.
/// </summary>
public sealed class HomematicPollingBackgroundService : BackgroundService
{
    private static readonly TimeSpan RetentionCleanupInterval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IUtcClock utcClock;
    private readonly ILogger<HomematicPollingBackgroundService> logger;
    private readonly HashSet<string> reportedIgnoredDeviceTypes = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? lastRetentionCleanupAtUtc;
    private bool missingTokenReported;

    public HomematicPollingBackgroundService(
        IServiceScopeFactory scopeFactory,
        IUtcClock utcClock,
        ILogger<HomematicPollingBackgroundService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.utcClock = utcClock;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var pollingIntervalSeconds = await PollOnceAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(pollingIntervalSeconds), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Runs one cycle and returns the interval until the next one, so setting changes apply without restart.
    /// </summary>
    private async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        var pollingIntervalSeconds = HomematicSettings.MinimumPollingIntervalSeconds;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var settings = await scope.ServiceProvider.GetRequiredService<DatabaseHomematicSettingsProvider>().GetSettingsAsync(cancellationToken);
            pollingIntervalSeconds = settings.PollingIntervalSeconds;

            if (!settings.IsEnabled)
            {
                return pollingIntervalSeconds;
            }

            if (!settings.IsReadyForPolling)
            {
                // Report once instead of every cycle; the operator has to act in the settings UI anyway.
                if (!missingTokenReported)
                {
                    logger.LogWarning("Homematic-Erfassung ist aktiviert, aber Basis-URL oder API-Token fehlen. Es wird nichts abgefragt.");
                    missingTokenReported = true;
                }

                return pollingIntervalSeconds;
            }

            missingTokenReported = false;
            var polledAtUtc = utcClock.UtcNow;
            var snapshot = await scope.ServiceProvider.GetRequiredService<IHomematicClimateSource>().GetCurrentSnapshotAsync(polledAtUtc, cancellationToken);
            var repository = scope.ServiceProvider.GetRequiredService<IHomematicClimateRepository>();
            await repository.SaveSamplesAsync(snapshot.Samples, cancellationToken);
            ReportNewIgnoredDeviceTypes(snapshot.IgnoredClimateDeviceTypes);
            await CleanUpOldSamplesIfDueAsync(repository, settings.SampleRetentionDays, polledAtUtc, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Homematic-Klimadaten konnten nicht abgerufen oder gespeichert werden. Naechster Versuch im naechsten Zyklus.");
        }

        return pollingIntervalSeconds;
    }

    private void ReportNewIgnoredDeviceTypes(IReadOnlyList<string> ignoredDeviceTypes)
    {
        foreach (var deviceType in ignoredDeviceTypes.Where(reportedIgnoredDeviceTypes.Add))
        {
            logger.LogInformation("Homematic-Geraetetyp {DeviceType} liefert Klimawerte, wird aber nicht erfasst, weil er nicht unterstuetzt ist.", deviceType);
        }
    }

    private async Task CleanUpOldSamplesIfDueAsync(
        IHomematicClimateRepository repository,
        int retentionDays,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (lastRetentionCleanupAtUtc is not null && nowUtc - lastRetentionCleanupAtUtc < RetentionCleanupInterval)
        {
            return;
        }

        var deletedCount = await repository.DeleteSamplesOlderThanAsync(nowUtc.AddDays(-retentionDays), cancellationToken);
        lastRetentionCleanupAtUtc = nowUtc;

        if (deletedCount > 0)
        {
            logger.LogInformation("{DeletedCount} Homematic-Klimadatensaetze aelter als {RetentionDays} Tage geloescht.", deletedCount, retentionDays);
        }
    }
}
