using TibberVictronController.Api.Dashboard;
using TibberVictronController.Api.EvCharging;
using TibberVictronController.Api.JointBattery;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

public sealed class DashboardChangeWatcherTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeWallbox : IEvChargerStatusSource
    {
        public EvChargerSnapshot Current { get; set; } = EvChargerSnapshot.Empty;
    }

    private readonly JointBatteryRuntimeState runtime = new();
    private readonly JointBatteryExecutionState execution = new();
    private readonly EvChargingControlState ev = new();
    private readonly FakeWallbox wallbox = new();

    private DashboardChangeWatcher CreateWatcher() => new(runtime, execution, ev, wallbox);

    [Fact]
    public void FirstLookOnlyTakesTheBaselineAndUnchangedStatesReportNothing()
    {
        var watcher = CreateWatcher();

        Assert.Empty(watcher.DetectChanges());
        Assert.Empty(watcher.DetectChanges());
    }

    [Fact]
    public void ReportsEachChangedAreaOnce()
    {
        var watcher = CreateWatcher();
        watcher.DetectChanges();

        runtime.Set(new JointBatteryRuntimeSnapshot("Active", "Plan berechnet.", NowUtc, null, null));
        wallbox.Current = wallbox.Current with { PowerWatts = 7200 };
        ev.Set(new EvChargingControlSnapshot(true, EvChargingMode.Plan, 10, "Günstiges Fenster.", NowUtc, null));

        Assert.Equal([DashboardChangeWatcher.JointArea, DashboardChangeWatcher.WallboxArea, DashboardChangeWatcher.EvArea], watcher.DetectChanges());
        Assert.Empty(watcher.DetectChanges());
    }

    [Fact]
    public void IgnoresMeasuredBatteryWattsAndRepeatedEvCyclesWithTheSameOutcome()
    {
        execution.Set(new JointBatteryExecutionSnapshot("Active", "Läuft.", NowUtc, true, 2000, 1000, 1950, 980));
        ev.Set(new EvChargingControlSnapshot(true, EvChargingMode.Plan, 10, "Günstiges Fenster.", NowUtc, null));
        var watcher = CreateWatcher();
        watcher.DetectChanges();

        // Same targets, new measurements and a later cycle time: nothing the dashboard has to reload.
        execution.Set(new JointBatteryExecutionSnapshot("Active", "Läuft.", NowUtc.AddSeconds(1), true, 2000, 1000, 2010, 1020));
        ev.Set(new EvChargingControlSnapshot(true, EvChargingMode.Plan, 10, "Günstiges Fenster.", NowUtc.AddSeconds(30), null));
        Assert.Empty(watcher.DetectChanges());

        execution.Set(new JointBatteryExecutionSnapshot("Active", "Läuft.", NowUtc.AddSeconds(2), true, 2500, 1000, 2010, 1020));
        Assert.Equal([DashboardChangeWatcher.JointArea], watcher.DetectChanges());
    }
}
