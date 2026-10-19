using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Services;

public sealed class WeishauptHotWaterReportService(
    IWeishauptHotWaterStateStore stateStore, IWeishauptBiblockTelemetryRepository telemetry, IUtcClock clock)
{
    public async Task<WeishauptHotWaterReport> GetReportAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var state = await stateStore.LoadAsync(ct);
        var samples = await telemetry.GetSamplesAsync(now.AddDays(-14), now.AddTicks(1), ct);
        var episodes = state.Episodes.Where(e => e.StartedAtUtc >= now.AddDays(-14)).ToArray();
        return new WeishauptHotWaterReport(now, state.OriginalCelsius.HasValue, state.Suspended, state.LastPlan,
            WeishauptHotWaterEpisodeAnalyzer.Analyze(episodes, samples, now).OrderByDescending(a => a.Episode.StartedAtUtc).ToArray(),
            samples.Where(s => s.MeasuredAtUtc >= now.AddDays(-1) && s.HotWaterTemperatureCelsius is >= 0m and <= 95m).ToArray());
    }
}

public sealed record WeishauptHotWaterReport(DateTimeOffset GeneratedAtUtc, bool IsRaised, bool IsSuspended,
    WeishauptHotWaterPricePlan? Plan, IReadOnlyList<WeishauptHotWaterEpisodeAnalysis> Episodes,
    IReadOnlyList<WeishauptBiblockTelemetrySample> Samples);
