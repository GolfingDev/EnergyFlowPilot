using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

/// <summary>
/// What the PV-surplus branch of the decider must know about the recent past, so that starting and stopping do not
/// follow every cloud: each flip cycles the wallbox contactor and the car's charger.
/// </summary>
public sealed record EvSurplusChargingState(bool IsCharging, DateTimeOffset? MinimumRunEndsAtUtc, DateTimeOffset? MinimumPauseEndsAtUtc)
{
    public static readonly EvSurplusChargingState None = new(false, null, null);
}

/// <summary>One live reading of the PV power the car could take.</summary>
public static class EvSurplusSample
{
    public static readonly TimeSpan TelemetryMaximumAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// PV power left for the car: grid export plus the car's own charging power, minus what the home batteries
    /// discharge. Without the last term a car fed by the home batteries would see "surplus" (grid import stays near
    /// zero) and keep emptying them. Battery charging is not added back: the home batteries take PV first, the car
    /// gets what would be exported (docs/20-domain/ev-charging.md, stage 4c).
    /// Null when a needed reading is missing or stale: unknown battery power must not count as "no discharge".
    /// </summary>
    public static double? AvailableWatts(CurrentSiteTelemetry telemetry, double wallboxPowerWatts, DateTimeOffset nowUtc)
    {
        if (nowUtc - telemetry.MeasuredAtUtc > TelemetryMaximumAge) return null;
        // Positive battery power means charging (as in WeishauptHotWaterPolicy).
        if (telemetry.CurrentBatteryPowerWatts is not { } victronBatteryWatts) return null;
        var discharge = Math.Max(0, -victronBatteryWatts);
        if (telemetry.SupplementaryBatteryMonitoringRequired)
        {
            // An installed E3/DC battery reports on its own schedule; its discharge value is 0 when nothing was read.
            if (telemetry.SupplementaryBatteryMeasuredAtUtc is not { } measured ||
                nowUtc - measured > SupplementaryGridChargeGuard.MaximumTelemetryAge) return null;
            discharge += Math.Max(0, telemetry.SupplementaryBatteryDischargeWatts);
        }

        return -telemetry.CurrentGridImportWatts + wallboxPowerWatts - discharge;
    }
}

/// <summary>
/// Remembers the surplus readings of the last minutes and when surplus charging started and charging last stopped.
/// The rules themselves are in <see cref="EvChargingDecider"/>; this class only keeps the state between cycles.
/// </summary>
public sealed class EvSurplusChargingTracker
{
    // Six samples at the 30 s control cycle are 0 to 150 s old; the slack absorbs a late cycle.
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(165);

    // Up to two of the six samples may be missing (a late telemetry message, a cycle blocked by a wallbox command):
    // one gap must not pause surplus charging for three minutes.
    public const int MinimumSamples = 4;

    // Once started, surplus charging keeps running this long while the shortfall stays bounded (see the decider).
    public static readonly TimeSpan MinimumRunTime = TimeSpan.FromMinutes(10);

    // After charging stopped, surplus alone does not start it again before this time.
    public static readonly TimeSpan MinimumPause = TimeSpan.FromMinutes(5);

    private readonly Queue<(DateTimeOffset AtUtc, double Watts)> samples = new();
    private DateTimeOffset? surplusSinceUtc;
    private DateTimeOffset? stoppedAtUtc;
    private bool wasCharging;

    /// <summary>Adds the reading of this cycle; a missing reading (null) simply leaves a gap.</summary>
    public void AddSample(DateTimeOffset nowUtc, double? availableWatts)
    {
        if (availableWatts is { } watts) samples.Enqueue((nowUtc, watts));
        Trim(nowUtc);
    }

    /// <summary>Mean of the window; null when too few readings exist (also for the first minutes after a restart).</summary>
    public double? AverageAvailableWatts(DateTimeOffset nowUtc)
    {
        Trim(nowUtc);
        return samples.Count >= MinimumSamples ? samples.Average(sample => sample.Watts) : null;
    }

    public EvSurplusChargingState State => new(
        surplusSinceUtc is not null,
        surplusSinceUtc + MinimumRunTime,
        stoppedAtUtc + MinimumPause);

    /// <summary>Records what the decider chose for this cycle.</summary>
    public void Observe(EvChargingDecision decision, DateTimeOffset nowUtc)
    {
        if (decision.Mode != EvChargingMode.PvSurplus) surplusSinceUtc = null;
        else surplusSinceUtc ??= nowUtc;

        // Amps null is full power; only 0 A opens the contactor.
        var charging = decision.Amps is not 0;
        if (wasCharging && !charging) stoppedAtUtc = nowUtc;
        wasCharging = charging;
    }

    /// <summary>
    /// Forgets run time and pause, for stops the surplus rules must never influence: a stop by hand, the automatic
    /// control switched off, the car unplugged. The readings stay, they describe the house, not the charging session.
    /// </summary>
    public void Reset()
    {
        surplusSinceUtc = null;
        stoppedAtUtc = null;
        wasCharging = false;
    }

    private void Trim(DateTimeOffset nowUtc)
    {
        while (samples.Count > 0 && nowUtc - samples.Peek().AtUtc > Window) samples.Dequeue();
    }
}
