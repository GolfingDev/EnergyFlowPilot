using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.EvCharging;

/// <summary>
/// Long-term cheap price for the car (setting "Günstig laden": percentile of the quarter-hour prices of the last 30
/// days). Computed once per UTC day: the history query aggregates many decision log rows, the plan runs every few
/// minutes.
/// </summary>
public sealed class EvCheapPriceThresholdCache(IUtcClock clock, ILogger<EvCheapPriceThresholdCache> logger)
{
    private const double DefaultPercentile = 25;
    private readonly SemaphoreSlim gate = new(1, 1);
    private (DateOnly Day, double Percentile, string Source)? cachedKey;
    private EvCheapPriceThreshold? cached;

    public async Task<EvCheapPriceThreshold> GetAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var settings = services.GetRequiredService<IControllerSettingStore>();
        var percentile = double.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.EvChargingCheapPercentileKey, cancellationToken))?.Value,
            NumberStyles.Float, CultureInfo.InvariantCulture, out var configured) ? Math.Clamp(configured, 0, 100) : DefaultPercentile;
        var provider = services.GetRequiredService<IElectricityPriceForecastProvider>();
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var key = (today, percentile, provider.Source);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is not null && cachedKey == key) return cached;
            var to = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var from = to.AddDays(-EvCheapPrice.HistoryDays);
            IReadOnlyList<TibberPriceForecastSlot> history;
            try
            {
                // As in the joint battery plan: generic tariffs rebuild past prices with today's tariff; otherwise the
                // logged prices count, but none from before a change of supplier.
                if (provider.Source == "energyCharts")
                    history = await provider.GetPriceForecastAsync(from, to, cancellationToken);
                else
                {
                    if (DateTimeOffset.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.ElectricityPriceProviderChangedAtKey, cancellationToken))?.Value,
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var changed) && changed > from)
                        from = changed.ToUniversalTime();
                    history = from >= to ? [] : await services.GetRequiredService<IDecisionLogRepository>().GetHistoricalPricesAsync(
                        new HistoricalPriceRequest(from, to, provider.Source, "EUR"), cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Not cached: the next plan tries again. Without a threshold the car fills up only with PV.
                logger.LogWarning("Preisverlauf für die Auto-Ladeschwelle nicht lesbar: {Reason}", exception.Message);
                return new EvCheapPriceThreshold(null, percentile, 0, "Preisverlauf nicht lesbar: " + exception.Message);
            }

            cached = EvCheapPrice.Threshold(history, percentile);
            cachedKey = key;
            logger.LogInformation("Auto-Ladeschwelle: {Percentile:0} % günstigste Viertelstunden der letzten {Days} Tage = {Price} EUR/kWh.",
                percentile, cached.HistoryDays, cached.PricePerKwh?.ToString("0.000", CultureInfo.InvariantCulture) ?? "keine");
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }
}
