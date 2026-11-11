using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Repositories;

namespace TibberVictronController.Api.Prices;

/// <summary>
/// Keeps the published price archive filled: every 15 minutes it stores the selected supplier's published prices of
/// the last 12 and the next 48 hours (providers cache them, so this costs no extra supplier requests in normal
/// operation). Once, it imports the prices logged with decisions since the oldest decision log entry, one UTC day at a
/// time with pauses so the Raspberry Pi stays responsive; progress survives restarts.
/// </summary>
public sealed class PublishedPriceArchiveBackgroundService(IServiceScopeFactory scopes, IUtcClock clock,
    ILogger<PublishedPriceArchiveBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private bool backfillDone;

    /// <summary>Pause between two imported days of the backfill.</summary>
    public TimeSpan BackfillPause { get; init; } = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the controller and its first plans start before adding database work.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RecordAsync(stoppingToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Supplier texts may contain URLs; the type is enough to see that recording failed.
                logger.LogWarning("Veroeffentlichte Preise konnten nicht archiviert werden. Fehlertyp={ErrorType}", exception.GetType().Name);
            }
            if (!backfillDone)
            {
                try { await BackfillAsync(stoppingToken); backfillDone = true; }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Preise aus dem Entscheidungslog konnten nicht ins Preisarchiv uebernommen werden.");
                }
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    /// <summary>Stores the currently published prices of the selected supplier.</summary>
    public async Task RecordAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IElectricityPriceForecastProvider>();
        var now = clock.UtcNow;
        var prices = await provider.GetPriceForecastAsync(now.AddHours(-12), now.AddHours(48), cancellationToken);
        await scope.ServiceProvider.GetRequiredService<IPublishedPriceArchive>().SaveAsync(provider.Source, prices, now, cancellationToken);
    }

    /// <summary>Imports logged prices of complete UTC days up to today, continuing where an earlier run stopped.</summary>
    public async Task BackfillAsync(CancellationToken cancellationToken)
    {
        var todayStart = new DateTimeOffset(clock.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        DateTimeOffset day;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IControllerSettingStore>();
            var stored = (await settings.GetSettingAsync(ControllerSettingDefaults.PriceArchiveBackfilledThroughKey, cancellationToken))?.Value;
            if (DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var through))
                day = through.ToUniversalTime();
            else if (await scope.ServiceProvider.GetRequiredService<EfPublishedPriceArchive>().GetOldestDecisionAtUtcAsync(cancellationToken) is { } oldest)
                day = new DateTimeOffset(oldest.UtcDateTime.Date, TimeSpan.Zero);
            else
                return;
        }

        var imported = 0;
        for (; day < todayStart; day = day.AddDays(1))
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                imported += await scope.ServiceProvider.GetRequiredService<EfPublishedPriceArchive>()
                    .ImportFromDecisionLogAsync(day, day.AddDays(1), clock.UtcNow, cancellationToken);
                await scope.ServiceProvider.GetRequiredService<IControllerSettingStore>().SaveSettingAsync(new ControllerSetting(
                    ControllerSettingDefaults.PriceArchiveBackfilledThroughKey, day.AddDays(1).ToString("O", CultureInfo.InvariantCulture),
                    ControllerSettingSensitivity.Normal, clock.UtcNow), cancellationToken);
            }
            await Task.Delay(BackfillPause, cancellationToken);
        }
        if (imported > 0)
            logger.LogInformation("Preisarchiv: {Count} Viertelstunden aus dem Entscheidungslog uebernommen.", imported);
    }
}
