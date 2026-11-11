using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Repositories;

namespace TibberVictronController.Api.Decision;

/// <summary>
/// Applies <c>decisionLog.retentionDays</c> once <c>decisionLog.cleanupEnabled</c> is on (default off: the deletion is
/// permanent, the operator switches it on after a backup). It never deletes entries that the energy history or the
/// price archive backfill has not processed yet, never keeps less than 30 days (the longest reader window, the car's
/// cheap-price threshold), and deletes in small batches with pauses so the Raspberry Pi and the joint execution stay
/// responsive. SQLite reuses the freed pages; the database file does not shrink without a VACUUM.
/// </summary>
public sealed class DecisionLogRetentionBackgroundService(IServiceScopeFactory scopes, IUtcClock clock,
    ILogger<DecisionLogRetentionBackgroundService> logger) : BackgroundService
{
    public const int MinimumRetentionDays = 30;
    public const int DefaultRetentionDays = 90;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private string? lastWaitReason;

    public int BatchSize { get; init; } = 1000;
    public int MaximumBatchesPerRun { get; init; } = 100;
    public TimeSpan BatchPause { get; init; } = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Entscheidungslog konnte nicht aufgeraeumt werden.");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    /// <returns>Number of deleted decisions in this run.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IControllerSettingStore>();
            async Task<string?> Value(string key) => (await settings.GetSettingAsync(key, cancellationToken))?.Value;
            if (!bool.TryParse(await Value(ControllerSettingDefaults.DecisionLogCleanupEnabledKey), out var enabled) || !enabled) return 0;
            var days = int.TryParse(await Value(ControllerSettingDefaults.DecisionLogRetentionDaysKey), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var configured) ? configured : DefaultRetentionDays;
            cutoff = clock.UtcNow.AddDays(-Math.Max(MinimumRetentionDays, days));
            foreach (var (key, name) in new[] { (ControllerSettingDefaults.EnergyHistoryBackfilledThroughKey, "Energieverlauf"),
                (ControllerSettingDefaults.PriceArchiveBackfilledThroughKey, "Preisarchiv") })
            {
                if (!DateTimeOffset.TryParse(await Value(key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var processedThrough))
                {
                    // Both backfills read the decision log from its oldest entry; deleting first would lose their data.
                    var reason = $"Aufraeumen wartet, bis die Nachberechnung '{name}' abgeschlossen ist.";
                    if (reason != lastWaitReason) logger.LogInformation("{Reason}", reason);
                    lastWaitReason = reason;
                    return 0;
                }
                if (processedThrough.ToUniversalTime() < cutoff) cutoff = processedThrough.ToUniversalTime();
            }
            lastWaitReason = null;
        }

        var total = 0;
        for (var batch = 0; batch < MaximumBatchesPerRun; batch++)
        {
            int deleted;
            await using (var scope = scopes.CreateAsyncScope())
                deleted = await scope.ServiceProvider.GetRequiredService<EfDecisionLogRepository>().DeleteOldestBatchAsync(cutoff, BatchSize, cancellationToken);
            total += deleted;
            if (deleted < BatchSize) break;
            await Task.Delay(BatchPause, cancellationToken);
        }
        if (total > 0)
            logger.LogInformation("Entscheidungslog aufgeraeumt: {Count} Eintraege vor {CutoffUtc:O} geloescht.", total, cutoff);
        return total;
    }
}
