using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Energy;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Energy;

/// <summary>
/// Keeps the permanent quarter-hour energy history up to date: once a backfill from the oldest decision log entry,
/// one UTC day at a time with pauses (the Pi must not stall; progress survives restarts), then every 15 minutes the
/// last hours and once per day the whole previous day. Raw data is deleted after a while; the stored history stays.
/// </summary>
public sealed class EnergyHistoryBackgroundService(IServiceScopeFactory scopes, IUtcClock clock, ILogger<EnergyHistoryBackgroundService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(3);
    private static readonly TimeSpan BackfillPause = TimeSpan.FromSeconds(5);
    private DateOnly finalizedDay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BackfillAsync(stoppingToken);
                var now = clock.UtcNow;
                await RecalculateAsync(now - RecentWindow, now, stoppingToken);
                var today = DateOnly.FromDateTime(now.UtcDateTime);
                if (finalizedDay != today)
                {
                    var dayStart = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                    await RecalculateAsync(dayStart.AddDays(-1), dayStart, stoppingToken);
                    finalizedDay = today;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Energieverlauf konnte nicht berechnet werden.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task BackfillAsync(CancellationToken cancellationToken)
    {
        var todayStart = new DateTimeOffset(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        DateTimeOffset day;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IControllerSettingStore>();
            var stored = (await settings.GetSettingAsync(ControllerSettingDefaults.EnergyHistoryBackfilledThroughKey, cancellationToken))?.Value;
            if (DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var through))
                day = through.ToUniversalTime();
            else if (await scope.ServiceProvider.GetRequiredService<IEnergyMeasurementReader>().GetFirstMeasurementAtUtcAsync(cancellationToken) is { } first)
                day = new DateTimeOffset(first.UtcDateTime.Date, TimeSpan.Zero);
            else
                return;
        }

        if (day >= todayStart) return;
        logger.LogInformation("Energieverlauf: Nachberechnung ab {Day:yyyy-MM-dd} (UTC).", day);
        var days = 0;
        for (; day < todayStart; day = day.AddDays(1))
        {
            var count = await RecalculateAsync(day, day.AddDays(1), cancellationToken);
            await using (var scope = scopes.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<IControllerSettingStore>().SaveSettingAsync(new ControllerSetting(
                    ControllerSettingDefaults.EnergyHistoryBackfilledThroughKey, day.AddDays(1).ToString("O", CultureInfo.InvariantCulture),
                    ControllerSettingSensitivity.Normal, clock.UtcNow), cancellationToken);
            if (++days % 10 == 0)
                logger.LogInformation("Energieverlauf: bis {Day:yyyy-MM-dd} nachberechnet ({Count} Viertelstunden am letzten Tag).", day, count);
            await Task.Delay(BackfillPause, cancellationToken);
        }

        logger.LogInformation("Energieverlauf: Nachberechnung abgeschlossen ({Days} Tage).", days);
    }

    private async Task<int> RecalculateAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EnergyHistoryService>().RecalculateAsync(fromUtc, toUtc, cancellationToken);
    }
}
