using TibberVictronController.Api.JointBattery;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Api.Prices;

/// <summary>The price course with a note when a part could not be loaded (the rest is still shown).</summary>
public sealed record PriceTimelineView(PriceTimelineResult Timeline, DateTimeOffset FromUtc, DateTimeOffset UntilUtc, string? Problem);

/// <summary>
/// Collects the price course for the dashboard: the last twelve hours as they applied, the supplier's prices and the
/// forecast up to 48 hours ahead (operator 2026-11-11).
/// </summary>
public sealed class PriceTimelineService(
    IElectricityPriceForecastProvider provider,
    IDecisionLogRepository decisions,
    IPriceForecastSnapshotRepository snapshots,
    IServiceProvider services,
    IUtcClock clock,
    ILogger<PriceTimelineService> logger)
{
    public static readonly TimeSpan PastWindow = TimeSpan.FromHours(12);
    public static readonly TimeSpan FutureWindow = TimeSpan.FromHours(48);

    public async Task<PriceTimelineView> GetAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var quarter = JointPlanInputFallback.QuarterStart(now);
        var (from, until) = (quarter - PastWindow, quarter + FutureWindow);
        var problems = new List<string>();

        var past = await TryAsync(() => provider.Source == "energyCharts"
                // Generic tariffs rebuild past prices from the exchange; otherwise the logged prices are what applied.
                ? provider.GetPriceForecastAsync(from, now, cancellationToken)
                : decisions.GetHistoricalPricesAsync(new HistoricalPriceRequest(from, now, provider.Source, "EUR"), cancellationToken),
            "Preise der letzten Stunden", problems);
        var upcoming = await TryAsync(() => provider.GetPriceForecastAsync(quarter, until, cancellationToken), "Aktuelle Preise", problems);
        // Day-ahead prices do not change: the last ones the battery planning loaded serve when the supplier fails.
        if (upcoming.Count == 0) upcoming = services.GetService<JointBatteryInputCache>()?.Prices.Items ?? [];
        var stored = await TryAsync(() => snapshots.GetAsync(from, until, cancellationToken), "Preisprognose", problems);

        var timeline = PriceTimeline.Build(new PriceTimelineInput(now, from, until, past, upcoming, RetailPriceForecast.Fresh(stored, now)));
        return new PriceTimelineView(timeline, from, until, problems.Count == 0 ? null : string.Join(" ", problems));
    }

    private async Task<IReadOnlyList<T>> TryAsync<T>(Func<Task<IReadOnlyList<T>>> load, string part, List<string> problems)
    {
        try
        {
            return await load();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Preisverlauf: {Part} nicht abrufbar: {Reason}", part, exception.Message);
            problems.Add($"{part} nicht abrufbar.");
            return [];
        }
    }
}
