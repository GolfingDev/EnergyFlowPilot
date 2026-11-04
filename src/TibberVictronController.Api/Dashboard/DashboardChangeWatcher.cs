using TibberVictronController.Api.EvCharging;
using TibberVictronController.Api.JointBattery;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Dashboard;

/// <summary>
/// Finds out which dashboard areas changed since the last look. It only reads the in-memory states the control
/// services already publish, so the control loops themselves stay untouched (no extra work in their cycle budget).
/// </summary>
public sealed class DashboardChangeWatcher(
    JointBatteryRuntimeState jointRuntime,
    JointBatteryExecutionState jointExecution,
    EvChargingControlState evControl,
    IEvChargerStatusSource wallbox)
{
    public const string JointArea = "joint";
    public const string WallboxArea = "wallbox";
    public const string EvArea = "ev";

    private object? lastJoint;
    private object? lastWallbox;
    private object? lastEv;

    /// <summary>Areas whose state differs from the previous call; the first call only takes the baseline.</summary>
    public IReadOnlyList<string> DetectChanges()
    {
        var changed = new List<string>();
        var runtime = jointRuntime.Current;
        var execution = jointExecution.Current;
        // Measured battery watts are left out: they arrive with every live sample anyway.
        // The plan time stamp covers a new joint plan; status, targets and the manual charge result cover the execution.
        Compare(ref lastJoint, (runtime.Status, runtime.UpdatedAtUtc, execution.Status, execution.OwnsControl,
            execution.VictronTargetWatts, execution.E3dcTargetWatts, jointExecution.ManualChargeResult), JointArea, changed);
        // The wallbox store replaces its snapshot on every message, so a new instance means new data.
        Compare(ref lastWallbox, wallbox.Current, WallboxArea, changed, byReference: true);
        var ev = evControl.Current;
        Compare(ref lastEv, (ev.Enabled, ev.Mode, ev.Amps, ev.Reason, ev.CommandProblem), EvArea, changed);
        return changed;
    }

    private static void Compare(ref object? last, object current, string area, List<string> changed, bool byReference = false)
    {
        var first = last is null;
        var same = byReference ? ReferenceEquals(last, current) : Equals(last, current);
        last = current;
        if (!first && !same) changed.Add(area);
    }
}

/// <summary>Looks for changes once per second and tells the dashboards which area to reload.</summary>
public sealed class DashboardChangeWatcherBackgroundService(
    DashboardChangeWatcher watcher,
    IDashboardLiveUpdatePublisher publisher,
    ILogger<DashboardChangeWatcherBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var area in watcher.DetectChanges())
                    {
                        await publisher.PublishChangedAsync(area, stoppingToken);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Dashboards fall back to their periodic resync; nothing in the control depends on this message.
                    logger.LogDebug(exception, "Änderungsmeldung an die Dashboards fehlgeschlagen.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
