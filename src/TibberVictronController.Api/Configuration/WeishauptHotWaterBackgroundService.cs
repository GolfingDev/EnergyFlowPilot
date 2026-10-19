using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;
using TibberVictronController.Dal.Weishaupt;

namespace TibberVictronController.Api.Configuration;

public sealed class WeishauptHotWaterBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<WeishauptHotWaterBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Weishaupt-Warmwasserautomatik fehlgeschlagen. Ruecknahme wird im naechsten Zyklus erneut geprueft.");
            }
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var now = services.GetRequiredService<IUtcClock>().UtcNow;
        var modbus = await services.GetRequiredService<DatabaseWeishauptBiblockModbusSettingsProvider>().GetSettingsAsync(ct);
        WeishauptHotWaterSettings settings;
        try
        {
            settings = await services.GetRequiredService<DatabaseWeishauptHotWaterSettingsProvider>().GetSettingsAsync(ct);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Weishaupt-Planungseinstellungen ungueltig; eigene Anhebung wird zurueckgenommen.");
            settings = new WeishauptHotWaterSettings();
        }
        settings = settings with { IsEnabled = settings.IsEnabled && modbus.IsReadyForPolling };
        var enabled = settings.IsEnabled;
        var state = await services.GetRequiredService<IWeishauptHotWaterStateStore>().LoadAsync(ct);
        IReadOnlyList<TibberPriceForecastSlot> prices = [];
        CurrentSiteTelemetry? telemetry = null;
        if (enabled)
        {
            // Each signal can independently authorize a boost. Failure of one must not hide the other.
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                prices = await services.GetRequiredService<ITibberPriceForecastProvider>().GetPriceForecastAsync(now, now.AddHours(28), timeout.Token);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Weishaupt-Automatik: aktueller Tibber-Preis nicht verfuegbar.");
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                telemetry = await services.GetRequiredService<ICurrentSiteTelemetryProvider>().GetCurrentSiteTelemetryAsync(timeout.Token);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Weishaupt-Automatik: aktuelle PV-Telemetrie nicht verfuegbar.");
            }
        }
        now = services.GetRequiredService<IUtcClock>().UtcNow;
        var samples = await services.GetRequiredService<IWeishauptBiblockTelemetryRepository>().GetSamplesAsync(now.AddDays(-14), now.AddTicks(1), ct);
        var analysis = WeishauptHotWaterEpisodeAnalyzer.Analyze(state.Episodes, samples, now);
        var plan = WeishauptHotWaterPricePlanner.Plan(settings, now, prices, analysis);
        await services.GetRequiredService<IWeishauptHotWaterStateStore>().SaveAsync(state with { LastPlan = plan }, ct);
        var activeEpisode = state.Episodes.LastOrDefault(e => e.StartedAtUtc == state.RaisedAtUtc && e.EndedAtUtc is null);
        var decisionPlan = activeEpisode?.Plan ?? plan;
        var price = prices.FirstOrDefault(p => p.TimeSlot.StartsAtUtc <= now && now < p.TimeSlot.EndsAtUtc);
        var boost = WeishauptHotWaterPolicy.ShouldBoost(settings, now, state.OriginalCelsius is not null, price, telemetry, decisionPlan);
        var pv = WeishauptHotWaterPolicy.ShouldBoost(settings, now, state.OriginalCelsius is not null, null, telemetry);
        var freshSample = samples.LastOrDefault(s => s.MeasuredAtUtc <= now && now - s.MeasuredAtUtc <= TimeSpan.FromMinutes(10));
        // PV is usable immediately even if prices are missing or the price window is later.
        var episodePlan = pv && state.OriginalCelsius is null
            ? plan with { StartsAtUtc = now, EndsAtUtc = now.AddMinutes(plan.ChargeMinutes),
                WindowPricePerKwh = null, EstimatedAdvantagePerKwh = null, Reason = "Aktueller PV-Ueberschuss; kein gemessener Kostenvorteil." }
            : decisionPlan;
        var reached = state.RaisedCelsius is { } raised && freshSample is not null &&
            freshSample.MeasuredAtUtc >= state.RaisedAtUtc && freshSample.HotWaterTemperatureCelsius >= raised - 0.5m;
        await services.GetRequiredService<WeishauptHotWaterControlService>().RunAsync(enabled, boost, now, ct,
            new WeishauptHotWaterBoostContext(episodePlan, pv ? "PV-Ueberschuss" : "Preisfenster", reached,
                freshSample?.HotWaterTemperatureCelsius));
    }
}
