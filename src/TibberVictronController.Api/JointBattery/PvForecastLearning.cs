using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

/// <summary>
/// Keeps the PV forecast correction: stores the latest forecast per quarter hour (at most every 15 minutes) and
/// recalculates the learned factors at most hourly from the stored forecasts and the measured PV.
/// </summary>
public sealed class PvForecastLearning(IUtcClock clock)
{
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RecalculationInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan SnapshotAhead = TimeSpan.FromHours(26);
    private static readonly TimeSpan LearningWindow = TimeSpan.FromDays(14);
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset snapshotWrittenAtUtc = DateTimeOffset.MinValue;
    private PvForecastCorrection correction = PvForecastCorrection.None;

    public PvForecastCorrection Current => Volatile.Read(ref correction);

    public async Task RecordForecastAsync(IServiceProvider services, IReadOnlyList<PvYieldForecastSlot> forecast, CancellationToken token)
    {
        var now = clock.UtcNow;
        if (now - snapshotWrittenAtUtc < SnapshotInterval) return;
        var snapshots = forecast
            .Where(slot => slot.IsForecastAvailable && slot.TimeSlot.StartsAtUtc > now && slot.TimeSlot.StartsAtUtc <= now + SnapshotAhead)
            .Select(slot => new PvForecastSnapshot(slot.TimeSlot.StartsAtUtc, slot.ExpectedPvYieldKwh))
            .ToArray();
        await services.GetRequiredService<IPvForecastSnapshotRepository>().UpsertAsync(snapshots, now, token);
        snapshotWrittenAtUtc = now;
    }

    /// <summary>Returns the cached correction, recalculated if it is older than an hour.</summary>
    public async Task<PvForecastCorrection> GetAsync(IServiceProvider services, IReadOnlyList<LiveConsumptionSample>? history, TimeZoneInfo zone,
        CancellationToken token)
    {
        var now = clock.UtcNow;
        if (now - Current.CalculatedAtUtc < RecalculationInterval) return Current;
        await gate.WaitAsync(token);
        try
        {
            if (now - Current.CalculatedAtUtc < RecalculationInterval) return Current;
            var forecasts = await services.GetRequiredService<IPvForecastSnapshotRepository>().GetAsync(now - LearningWindow, now, token);
            history ??= await services.GetRequiredService<ILiveConsumptionRepository>().GetSamplesAsync(now - LearningWindow, now, token);
            var learned = PvForecastCorrectionLearner.Learn(forecasts, history, zone, now);
            Volatile.Write(ref correction, learned);
            return learned;
        }
        finally
        {
            gate.Release();
        }
    }
}
