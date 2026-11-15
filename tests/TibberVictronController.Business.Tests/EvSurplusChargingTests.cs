using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class EvSurplusChargingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 5, 0, TimeSpan.Zero);
    private const double Allowance = 1000;

    // --- One reading (fix: home battery discharge is no PV surplus) ---

    [Fact]
    public void ExportPlusTheCarsOwnPowerIsAvailable()
    {
        Assert.Equal(5000, EvSurplusSample.AvailableWatts(Telemetry(gridImport: -860, victronBattery: 0), 4140, Now));
    }

    [Fact]
    public void CarFedByTheHomeBatteriesSeesNoSurplus()
    {
        // The batteries cover the car: nothing is imported, and without the correction 4140 W would look available.
        var victronOnly = Telemetry(gridImport: 0, victronBattery: -4140);
        var both = Telemetry(gridImport: 0, victronBattery: -2000) with
        {
            SupplementaryBatteryMonitoringRequired = true,
            SupplementaryBatteryMeasuredAtUtc = Now.AddSeconds(-20),
            SupplementaryBatteryDischargeWatts = 2140
        };

        Assert.Equal(0, EvSurplusSample.AvailableWatts(victronOnly, 4140, Now));
        Assert.Equal(0, EvSurplusSample.AvailableWatts(both, 4140, Now));
    }

    [Fact]
    public void BatteryChargingIsNotOfferedToTheCar()
    {
        // Home batteries first: 3 kW go into the battery, only the 1 kW still exported is the car's.
        Assert.Equal(1000, EvSurplusSample.AvailableWatts(Telemetry(gridImport: -1000, victronBattery: 3000), 0, Now));
    }

    [Fact]
    public void MissingOrStaleReadingsGiveNoSample()
    {
        var withE3dc = Telemetry(gridImport: -5000, victronBattery: 0) with { SupplementaryBatteryMonitoringRequired = true };

        Assert.Null(EvSurplusSample.AvailableWatts(Telemetry(gridImport: -5000, victronBattery: null), 0, Now));
        Assert.Null(EvSurplusSample.AvailableWatts(Telemetry(gridImport: -5000, victronBattery: 0, age: TimeSpan.FromMinutes(3)), 0, Now));
        Assert.Null(EvSurplusSample.AvailableWatts(withE3dc, 0, Now));
        Assert.Null(EvSurplusSample.AvailableWatts(withE3dc with { SupplementaryBatteryMeasuredAtUtc = Now.AddMinutes(-4) }, 0, Now));
        Assert.Equal(5000, EvSurplusSample.AvailableWatts(withE3dc with { SupplementaryBatteryMeasuredAtUtc = Now.AddMinutes(-1) }, 0, Now));
    }

    // --- The window (fix: one missing reading must not pause for three minutes) ---

    [Fact]
    public void WindowToleratesTwoMissingReadingsOfSix()
    {
        var tracker = new EvSurplusChargingTracker();
        var at = Now.AddSeconds(-150);
        foreach (var watts in new double?[] { 5000, null, 6000, 5000, null, 4000 })
        {
            tracker.AddSample(at, watts);
            at = at.AddSeconds(30);
        }

        Assert.Equal(5000, tracker.AverageAvailableWatts(Now));
    }

    [Fact]
    public void WindowWithThreeMissingReadingsGivesNoAverage()
    {
        var tracker = new EvSurplusChargingTracker();
        var at = Now.AddSeconds(-150);
        foreach (var watts in new double?[] { 5000, null, 6000, null, null, 4000 })
        {
            tracker.AddSample(at, watts);
            at = at.AddSeconds(30);
        }

        Assert.Null(tracker.AverageAvailableWatts(Now));
    }

    [Fact]
    public void ReadingsFromBeforeAnInterruptionAreNotAveragedIn()
    {
        var tracker = new EvSurplusChargingTracker();
        for (var i = 0; i < 6; i++) tracker.AddSample(Now.AddMinutes(-60).AddSeconds(30 * i), 9000);

        tracker.AddSample(Now, 0);

        Assert.Null(tracker.AverageAvailableWatts(Now));
    }

    // --- Hysteresis, minimum run time, minimum pause (decider, surplus branch only) ---

    [Fact]
    public void StartingNeedsMoreSurplusThanContinuing()
    {
        // 6 A would take 740 W from the grid: within the allowance, but without the 300 W starting margin.
        var paused = Decide(3400, EvSurplusChargingState.None);
        var running = Decide(3400, new EvSurplusChargingState(true, null, null));

        Assert.Equal(EvChargingMode.Paused, paused.Mode);
        Assert.Equal(EvChargingMode.PvSurplus, running.Mode);
        Assert.Equal(6, running.Amps);
    }

    [Fact]
    public void MinimumRunTimeBridgesAModerateDropOnly()
    {
        var withinRunTime = new EvSurplusChargingState(true, Now.AddMinutes(4), null);

        var bridged = Decide(2500, withinRunTime);            // lacks 1640 W: more than the allowance, less than 2 kW
        var tooDeep = Decide(2000, withinRunTime);            // lacks 2140 W
        var noSurplusAtAll = Decide(-500, withinRunTime);
        var runTimeOver = Decide(2500, withinRunTime with { MinimumRunEndsAtUtc = Now.AddSeconds(-1) });
        var noReading = Decide(null, withinRunTime);

        Assert.Equal((EvChargingMode.PvSurplus, 6d), (bridged.Mode, bridged.Amps!.Value));
        Assert.Contains("Mindestlaufzeit", bridged.Reason);
        Assert.Equal(EvChargingMode.Paused, tooDeep.Mode);
        Assert.Equal(EvChargingMode.Paused, noSurplusAtAll.Mode);
        Assert.Equal(EvChargingMode.Paused, runTimeOver.Mode);
        // Without readings the shortfall cannot be bounded.
        Assert.Equal(EvChargingMode.Paused, noReading.Mode);
    }

    [Fact]
    public void MinimumPauseDelaysASurplusStartButNeverPlannedCharging()
    {
        var pausing = new EvSurplusChargingState(false, null, Now.AddMinutes(2));

        var surplusOnly = Decide(8000, pausing);
        var afterThePause = Decide(8000, pausing with { MinimumPauseEndsAtUtc = Now });
        var trip = EvChargingDecider.Decide(new EvChargingDecisionInput(Now, Plan(EvChargeReason.Trip, 11000, 2.75), null, 8000)
        {
            PvGridAllowanceWatts = Allowance,
            Surplus = pausing
        });

        Assert.Equal(EvChargingMode.Paused, surplusOnly.Mode);
        Assert.Contains("Mindestpause", surplusOnly.Reason);
        Assert.Equal((EvChargingMode.PvSurplus, 11d), (afterThePause.Mode, afterThePause.Amps!.Value));
        Assert.Equal((EvChargingMode.Plan, 16d), (trip.Mode, trip.Amps!.Value));
    }

    [Fact]
    public void PlannedChargingIsNotReducedByTheSurplusRules()
    {
        // Surplus held at 6 A by the minimum run time, while the plan wants 16 A for a trip.
        var decision = EvChargingDecider.Decide(new EvChargingDecisionInput(Now, Plan(EvChargeReason.Trip, 11000, 2.75), null, 2500)
        {
            PvGridAllowanceWatts = Allowance,
            Surplus = new EvSurplusChargingState(true, Now.AddMinutes(4), null)
        });

        Assert.Equal((EvChargingMode.Plan, 16d), (decision.Mode, decision.Amps!.Value));
    }

    // --- Tracker and decider together, as the control cycle uses them ---

    [Fact]
    public void PassingCloudsDoNotCycleTheContactor()
    {
        var tracker = new EvSurplusChargingTracker();
        var at = Now;
        var modes = new List<(TimeSpan After, EvChargingMode Mode)>();
        EvChargingMode? last = null;

        // Readings per 30 s cycle: sun, a moderate dip inside the minimum run time, a deep drop, sun again.
        void Run(int cycles, double available)
        {
            for (var i = 0; i < cycles; i++)
            {
                tracker.AddSample(at, available);
                var decision = EvChargingDecider.Decide(new EvChargingDecisionInput(at, Plan(null, 0, 0), null, tracker.AverageAvailableWatts(at))
                {
                    PvGridAllowanceWatts = Allowance,
                    Surplus = tracker.State
                });
                tracker.Observe(decision, at);
                if (decision.Mode != last) modes.Add((at - Now, decision.Mode));
                last = decision.Mode;
                at = at.AddSeconds(30);
            }
        }

        Run(10, 6000);   // starts after four readings
        Run(10, 2600);   // dip: bridged by the minimum run time
        Run(4, 500);     // deep drop: stops although the run time is not over
        Run(20, 6000);   // sun again after two minutes: waits for the minimum pause

        Assert.Equal(4, modes.Count);
        Assert.Equal((TimeSpan.Zero, EvChargingMode.Paused), modes[0]);
        Assert.Equal((TimeSpan.FromSeconds(90), EvChargingMode.PvSurplus), modes[1]);
        // The dip (minutes 5 to 10) did not stop it; the deep drop did, before the ten minutes of run time were over.
        Assert.Equal((TimeSpan.FromSeconds(630), EvChargingMode.Paused), modes[2]);
        // The surplus is back within three minutes, but the restart keeps the five-minute pause.
        Assert.Equal((TimeSpan.FromSeconds(630) + EvSurplusChargingTracker.MinimumPause, EvChargingMode.PvSurplus), modes[3]);
    }

    [Fact]
    public void ResetForgetsRunTimeAndPauseButKeepsTheReadings()
    {
        var tracker = new EvSurplusChargingTracker();
        for (var i = 0; i < 6; i++) tracker.AddSample(Now.AddSeconds(30 * i - 150), 6000);
        tracker.Observe(new EvChargingDecision(EvChargingMode.PvSurplus, 6, "Test"), Now);
        tracker.Observe(new EvChargingDecision(EvChargingMode.Paused, 0, "Test"), Now.AddSeconds(30));
        Assert.Equal(Now.AddSeconds(30) + EvSurplusChargingTracker.MinimumPause, tracker.State.MinimumPauseEndsAtUtc);

        tracker.Reset();

        Assert.Equal(EvSurplusChargingState.None, tracker.State);
        Assert.Equal(6000, tracker.AverageAvailableWatts(Now.AddSeconds(30)));
    }

    private static EvChargingDecision Decide(double? available, EvSurplusChargingState state) =>
        EvChargingDecider.Decide(new EvChargingDecisionInput(Now, Plan(null, 0, 0), null, available)
        {
            PvGridAllowanceWatts = Allowance,
            Surplus = state
        });

    private static EvChargePlan Plan(EvChargeReason? reason, double powerWatts, double gridKwh)
    {
        var start = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        EvPlannedChargeSlot[] slots = reason is { } value
            ? [new EvPlannedChargeSlot(start, start.AddMinutes(15), powerWatts, gridKwh, 0, 0.30, false, value)]
            : [];
        return new EvChargePlan(Now, slots, gridKwh, 0, 0, []);
    }

    private static CurrentSiteTelemetry Telemetry(int gridImport, int? victronBattery, TimeSpan? age = null) =>
        new(gridImport, 6000, Now - (age ?? TimeSpan.FromSeconds(10)), victronBattery);
}
