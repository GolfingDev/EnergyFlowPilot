using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Api.Prices;

/// <summary>Last fetch of the price forecast, for the settings page.</summary>
public sealed record PriceForecastFetchStatus(DateTimeOffset? LastFetchAtUtc, DateTimeOffset? ForecastUntilUtc, int ForecastQuarters, string? Problem)
{
    public static readonly PriceForecastFetchStatus None = new(null, null, 0, null);

    /// <summary>Settings the problem names; the page offers to enter them.</summary>
    public IReadOnlyList<string> MissingSettings { get; init; } = [];
}

public sealed class PriceForecastShadowState
{
    private PriceForecastFetchStatus status = PriceForecastFetchStatus.None;

    public PriceForecastFetchStatus Status => Volatile.Read(ref status);

    public void Set(PriceForecastFetchStatus next) => Volatile.Write(ref status, next);
}

/// <summary>
/// Shadow operation of the price forecast (operator 2026-11-06): every three hours the forecast and the published
/// exchange prices are stored, so its accuracy per lead time can be measured before the car planning uses it.
/// About 8 of the 500 daily calls of the Pro plan; nothing is controlled from it.
/// </summary>
public sealed class PriceForecastShadowService(IServiceScopeFactory scopes, PriceForecastShadowState state, IUtcClock clock,
    ILogger<PriceForecastShadowService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(3);
    // Without a token the next look comes soon, so a token entered on the page is used without waiting three hours.
    private static readonly TimeSpan WithoutTokenInterval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the start-up (database, MQTT) settle first.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await FetchAsync(stoppingToken);
            var withoutToken = state.Status.MissingSettings.Contains(ControllerSettingDefaults.PriceForecastTokenKey);
            await Task.Delay(withoutToken ? WithoutTokenInterval : Interval, stoppingToken);
        }
    }

    private async Task FetchAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var points = await scope.ServiceProvider.GetRequiredService<IPriceForecastSource>().GetAsync(cancellationToken);
            if (points is null)
            {
                state.Set(PriceForecastFetchStatus.None with
                {
                    Problem = "Kein energyforecast.de-Token eingetragen.", MissingSettings = [ControllerSettingDefaults.PriceForecastTokenKey]
                });
                return;
            }

            await scope.ServiceProvider.GetRequiredService<IPriceForecastSnapshotRepository>()
                .UpsertAsync(PriceForecastAccuracy.Snapshots(points, now), cancellationToken);
            var forecast = points.Where(point => point.IsForecast).ToArray();
            DateTimeOffset? until = forecast.Length == 0 ? null : forecast.Max(point => point.SlotStartUtc).AddMinutes(15);
            state.Set(new PriceForecastFetchStatus(now, until, forecast.Length, null));
            logger.LogInformation("Preisprognose gespeichert: {Forecast} Prognose-Viertelstunden bis {Until:yyyy-MM-dd HH:mm} UTC, {Market} Börsenpreise.",
                forecast.Length, until, points.Count - forecast.Length);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Preisprognose nicht abrufbar: {Reason}", exception.Message);
            state.Set(state.Status with { Problem = exception.Message, MissingSettings = MissingSettingException.KeysOf(exception) });
        }
    }
}
