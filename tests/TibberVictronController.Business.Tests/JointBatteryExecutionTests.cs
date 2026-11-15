using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class JointBatteryExecutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 17, 0, 0, TimeSpan.Zero);
    private static JointBatteryUnit Unit(string id, int power = 0, decimal soc = 50) => new(id,
        new BatteryConfiguration(new BatteryConfigurationValues { TotalCapacityKwh = 10, MaximumChargePowerWatts = 3000,
            MaximumDischargePowerWatts = 3000, MinimumStateOfChargePercent = 10, PlanningMinimumStateOfChargePercent = 10,
            PlanningMaximumStateOfChargePercent = 95 }), new(soc, Now), power);
    private static JointBatteryLiveSnapshot Live(int primary = 0, int secondary = 0) => new(Now,
        [Unit("victron", primary), Unit("e3dc", secondary)], [1000 + primary + secondary / 3, 1000 + secondary / 3, 1000 + secondary / 3],
        [200], primary, secondary > 0 ? 2 : secondary < 0 ? 1 : 0, new([10000, 10000, 10000], 5000, 1, 0));
    private static JointBatteryCommand Command(int a = 500, int b = 500) => new(a, b, a + b > 0 ? BatteryChargeSource.Grid : null, Now.AddMinutes(1), "Testvorgabe");

    [Fact]
    public async Task NeutralReadbacksPrecedeBothChargeCommands()
    {
        var hardware = new Hardware(); var state = new JointBatteryExecutionState();
        var coordinator = new JointBatteryExecutionCoordinator(hardware, state, new Clock());
        await coordinator.StepAsync(Command(), CancellationToken.None);
        await coordinator.StepAsync(Command(), CancellationToken.None);
        Assert.DoesNotContain(hardware.Commands, c => c.StartsWith("E3dc:Charge"));
        await coordinator.StepAsync(Command(), CancellationToken.None);
        Assert.Equal("Active", state.Current.Status);
        Assert.Equal(250, state.Current.VictronTargetWatts);
        Assert.Equal(250, state.Current.E3dcTargetWatts);
        Assert.True(hardware.Commands.IndexOf("E3dc:ChargeGrid:250") < hardware.Commands.IndexOf("Victron:250"));
    }

    [Fact]
    public async Task DirectionChangeStopsBothBeforeAnyDischarge()
    {
        var hardware = new Hardware(); var state = new JointBatteryExecutionState();
        var coordinator = new JointBatteryExecutionCoordinator(hardware, state, new Clock());
        for (var i = 0; i < 4; i++) await coordinator.StepAsync(Command(), CancellationToken.None);
        hardware.Commands.Clear();
        await coordinator.StepAsync(Command(-500, -500), CancellationToken.None);
        Assert.Equal("Transition", state.Current.Status);
        Assert.Equal("Victron:0", hardware.Commands[0]);
        Assert.DoesNotContain(hardware.Commands, c => c.StartsWith("E3dc:Discharge"));
        for (var i = 0; i < 3; i++) await coordinator.StepAsync(Command(-500, -500), CancellationToken.None);
        Assert.Equal("Active", state.Current.Status);
        Assert.True(state.Current.VictronTargetWatts < 0 && state.Current.E3dcTargetWatts < 0);
    }

    [Fact]
    public async Task PvChargeIsConfirmedByEmsChargeMode()
    {
        // PV charging must accept EMS_MODE 2; expecting 1 left PV slots stuck in "Starting" until the 90 s abort.
        // 4.5 kW export: PV charging is only allowed from measured surplus.
        var hardware = new Hardware { Adjust = live => live with { PhaseGridWatts = [-1500, -1500, -1500] } };
        var state = new JointBatteryExecutionState { NeutralSamples = 2, Direction = 1 };
        var command = Command() with { ChargeSource = BatteryChargeSource.PV };
        await new JointBatteryExecutionCoordinator(hardware, state, new Clock()).StepAsync(command, CancellationToken.None);
        Assert.Equal("Active", state.Current.Status);
        Assert.Contains("E3dc:ChargePv:250", hardware.Commands);
        Assert.Equal(250, state.Current.VictronTargetWatts);
    }

    [Fact]
    public async Task UnderDeliveryAfterSettlingIsReportedNotStopped()
    {
        // Seen on the device: the E3/DC confirms the power but delivers far less; that must not stop the joint charge.
        var hardware = new Hardware { Follow = false, Primary = 240, Secondary = 50 };
        var state = new JointBatteryExecutionState { NeutralSamples = 2, Direction = 1, SettlingSinceUtc = Now.AddSeconds(-100) };
        state.Set(new("Active", "", Now, true, 250, 250));
        await new JointBatteryExecutionCoordinator(hardware, state, new Clock()).StepAsync(Command(250, 250), CancellationToken.None);
        Assert.Equal("Active", state.Current.Status);
        Assert.Contains("E3/DC folgt nur teilweise: Soll 250 W, gemessen 50 W", state.Current.Message);
        Assert.DoesNotContain("Victron folgt", state.Current.Message);
    }

    [Fact]
    public async Task OvershootAfterSettlingStopsAndNamesTheBattery()
    {
        var hardware = new Hardware { Follow = false, Primary = 900, Secondary = 250 };
        var state = new JointBatteryExecutionState { NeutralSamples = 2, Direction = 1, SettlingSinceUtc = Now.AddSeconds(-100) };
        state.Set(new("Active", "", Now, true, 250, 250));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new JointBatteryExecutionCoordinator(hardware, state, new Clock()).StepAsync(Command(250, 250), CancellationToken.None));
        Assert.Contains("Victron ueberschreitet den Sollwert deutlich: Soll 250 W", error.Message);
        Assert.Contains("gemessen 900 W", error.Message);
    }

    [Theory]
    // The target was lowered in this step; the measurement still shows the previous one (old rule: 2500 + 500 < 3100).
    [InlineData(-2900, -2500, -3100)]
    // Discharging draws more DC power than the AC target (inverter losses); seen on 29.10.2026 (old rule: 2647 < 2730).
    [InlineData(-2147, -2147, -2730)]
    public void LoweredTargetAndInverterLossesAreNoOvershoot(int previousTarget, int target, int measured)
    {
        var hardware = new Hardware { Follow = false, Primary = measured };
        var state = new JointBatteryExecutionState { NeutralSamples = 2, Direction = -1, SettlingSinceUtc = Now.AddSeconds(-100) };
        state.Set(new("Active", "", Now, true, previousTarget, 0));
        // House demand large enough that the limiter keeps the requested discharge.
        hardware.Adjust = live => live with { PhaseGridWatts = [6000, 0, 0] };
        var exception = Record.ExceptionAsync(() =>
            new JointBatteryExecutionCoordinator(hardware, state, new Clock()).StepAsync(Command(target, 0) with { ChargeSource = null }, CancellationToken.None));
        Assert.Null(exception.Result);
        Assert.Equal("Active", state.Current.Status);
    }

    [Fact]
    public async Task ImplausibleE3dcEchoAboveRequestPreventsVictronStart()
    {
        var hardware = new Hardware(); var state = new JointBatteryExecutionState { NeutralSamples = 2, Direction = 1 };
        hardware.BadEcho = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JointBatteryExecutionCoordinator(hardware, state, new Clock()).StepAsync(Command(), CancellationToken.None));
        Assert.DoesNotContain(hardware.Commands, c => c == "Victron:250");
    }

    [Fact]
    public async Task LowerE3dcGrantIsUsedAndReportedInsteadOfStopping()
    {
        // As on the device at its reserve: 97 W granted for a larger request.
        var hardware = new Hardware { Grant = 97 }; var state = new JointBatteryExecutionState { NeutralSamples = 2, Direction = 1 };
        await new JointBatteryExecutionCoordinator(hardware, state, new Clock()).StepAsync(Command(), CancellationToken.None);
        Assert.Equal("Active", state.Current.Status);
        Assert.Equal(97, state.Current.E3dcTargetWatts);
        Assert.Equal(250, state.Current.VictronTargetWatts);
        Assert.Contains("E3/DC gibt nur 97 von 250 W frei", state.Current.Message);
        // The next request keeps trying above the grant (ramp from the granted power).
        await new JointBatteryExecutionCoordinator(hardware, state, new Clock()).StepAsync(Command(), CancellationToken.None);
        Assert.Contains("E3dc:ChargeGrid:347", hardware.Commands);
    }

    [Fact]
    public async Task ModeDelayKeepsVictronNeutral()
    {
        var hardware = new Hardware { DelayMode = true };
        var state = new JointBatteryExecutionState { NeutralSamples = 2, Direction = 1 };
        await new JointBatteryExecutionCoordinator(hardware, state, new Clock()).StepAsync(Command(), CancellationToken.None);
        Assert.Equal("Starting", state.Current.Status);
        Assert.Equal(0, state.Current.VictronTargetWatts);
        Assert.Equal("Victron:0", hardware.Commands[^1]);
    }

    [Fact]
    public async Task ReleaseDoesNotReturnE3dcToAutomaticWhileEitherBatteryStillMovesEnergy()
    {
        var hardware = new Hardware { Follow = false, Primary = 500 };
        var coordinator = new JointBatteryExecutionCoordinator(hardware, new(), new Clock());
        Assert.False(await coordinator.ReleaseAsync(CancellationToken.None));
        Assert.DoesNotContain("E3dc:Automatic:0", hardware.Commands);
        hardware.Primary = 0;
        Assert.True(await coordinator.ReleaseAsync(CancellationToken.None));
        Assert.Equal("E3dc:Automatic:0", hardware.Commands[^1]);
    }

    [Fact]
    public async Task FailedE3dcStopStillInhibitsVictronFirst()
    {
        var hardware = new Hardware { FailE3dc = true };
        await Assert.ThrowsAsync<IOException>(() => new JointBatteryExecutionCoordinator(hardware, new(), new Clock()).ReleaseAsync(CancellationToken.None));
        Assert.Equal("Victron:0", hardware.Commands[0]);
    }

    [Fact]
    public void UnknownPhaseLimitsBlockRealCommands()
    {
        var live = Live() with { Limits = new([0, 10000, 10000], 5000, 1, 0) };
        Assert.Throws<InvalidOperationException>(() => JointBatteryLiveLimiter.Limit(Command(), live, Now));
    }

    [Fact]
    public void L1LimitBindsEvenIfTotalSiteBudgetHasRoom()
    {
        var live = Live() with { Limits = new([1500, 10000, 10000], 5000, 1, 20000) };
        var limited = JointBatteryLiveLimiter.Limit(Command(3000, 3000), live, Now);
        Assert.InRange(limited.VictronWatts + limited.E3dcWatts, 0, 700);
    }

    [Fact]
    public void AcOutLoadConsumesTheVictronInputBudget()
    {
        var live = Live() with { AcOutWatts = [4700], Limits = new([10000, 10000, 10000], 5000, 1, 0) };
        Assert.InRange(JointBatteryLiveLimiter.Limit(Command(3000, 0), live, Now).VictronWatts, 0, 200);
    }

    [Fact]
    public void DischargeCannotExceedActualHouseLoadOrCreateBatteryExport()
    {
        var limited = JointBatteryLiveLimiter.Limit(Command(-3000, -3000), Live(), Now);
        Assert.InRange(-(limited.VictronWatts + limited.E3dcWatts), 0, 2900);
        var export = Live(-2000, 0) with { PhaseGridWatts = [-1000, 0, 0] };
        Assert.Equal(0, JointBatteryLiveLimiter.Limit(Command(-500, -500), export, Now).VictronWatts);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-500, -500)]
    public void PvSurplusIsStoredInsteadOfExportedWhenHoldingOrDischarging(int victron, int e3dc)
    {
        // 3 kW export with idle batteries (operator report 24.10.2026: surplus went to the grid while the plan said hold).
        var live = Live() with { PhaseGridWatts = [-1000, -1000, -1000] };
        var limited = JointBatteryLiveLimiter.Limit(Command(victron, e3dc) with { ChargeSource = null }, live, Now);
        Assert.Equal(BatteryChargeSource.PV, limited.ChargeSource);
        Assert.True(limited.VictronWatts > 0 && limited.E3dcWatts > 0);
        // Never more than the surplus itself: charging must not start drawing from the grid.
        var acCharge = limited.VictronWatts + limited.E3dcWatts / (decimal)Math.Sqrt(.9);
        Assert.InRange(acCharge, 2500m, 2900m);
        Assert.Contains("PV-Ueberschuss", limited.Reason);
    }

    [Fact]
    public void NoSurplusKeepsHoldAndGridChargeIsNotChanged()
    {
        Assert.Equal(0, JointBatteryLiveLimiter.Limit(Command(0, 0) with { ChargeSource = null }, Live(), Now).VictronWatts);
        var surplus = Live() with { PhaseGridWatts = [-1000, -1000, -1000] };
        var gridCharge = JointBatteryLiveLimiter.Limit(Command(400, 300), surplus, Now);
        Assert.Equal(BatteryChargeSource.Grid, gridCharge.ChargeSource);
        Assert.Equal(400, gridCharge.VictronWatts);
        Assert.Equal(300, gridCharge.E3dcWatts);
    }

    [Fact]
    public void ExpiredCommandFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => JointBatteryLiveLimiter.Limit(Command() with { ValidUntilUtc = Now }, Live(), Now));
    }

    [Fact]
    public void CommandValidityIsJudgedAtSelectionButMeasurementsAgainstTheCurrentTime()
    {
        // Selected 200 ms before the quarter hour ends, limited 400 ms later after reading both devices.
        var command = Command() with { ValidUntilUtc = Now.AddMilliseconds(200) };
        var later = Now.AddMilliseconds(400);
        Assert.Equal(500, JointBatteryLiveLimiter.Limit(command, Live(), later, Now).VictronWatts);

        // A command that was already over when it was selected still fails closed.
        Assert.Throws<InvalidOperationException>(() => JointBatteryLiveLimiter.Limit(command, Live(), later, later));
        // A selection must not be carried into later ticks, even inside the command's validity.
        var stalled = Now.AddSeconds(11);
        var freshLive = Live() with { MeasuredAtUtc = stalled, Batteries = [Unit("victron") with { State = new(50, stalled) }, Unit("e3dc") with { State = new(50, stalled) }] };
        Assert.Throws<InvalidOperationException>(() => JointBatteryLiveLimiter.Limit(Command(), freshLive, stalled, Now));
        // Measurement freshness is unchanged: 5 s against the current time, whatever the selection instant.
        Assert.Throws<InvalidOperationException>(() => JointBatteryLiveLimiter.Limit(Command(), Live(), Now.AddSeconds(6), Now.AddSeconds(6)));
        Assert.Throws<InvalidOperationException>(() => JointBatteryLiveLimiter.Limit(Command(), Live(), Now.AddSeconds(6), Now));
    }

    [Fact]
    public async Task SlotEndingDuringTheDeviceReadDoesNotFailTheStep()
    {
        var clock = new MovingClock();
        var hardware = new Hardware { BeforeRead = () => clock.UtcNow = Now.AddMilliseconds(400) };
        var state = new JointBatteryExecutionState { NeutralSamples = 2, Direction = 1 };
        var command = Command() with { ValidUntilUtc = Now.AddMilliseconds(200) };

        var outcome = await new JointBatteryExecutionCoordinator(hardware, state, clock).StepAsync(command, Now, CancellationToken.None);

        Assert.Equal(JointBatteryStepOutcome.Executed, outcome);
        Assert.Equal("Active", state.Current.Status);
    }

    private static async Task<(Hardware Hardware, JointBatteryExecutionState State, MovingClock Clock, JointBatteryExecutionCoordinator Coordinator)> ActiveChargeAsync()
    {
        var hardware = new Hardware(); var state = new JointBatteryExecutionState(); var clock = new MovingClock();
        var coordinator = new JointBatteryExecutionCoordinator(hardware, state, clock);
        for (var i = 0; i < 3; i++) await coordinator.StepAsync(Command(), CancellationToken.None);
        Assert.Equal("Active", state.Current.Status);
        hardware.Commands.Clear();
        return (hardware, state, clock, coordinator);
    }

    [Fact]
    public async Task OneMissedReadLeavesConfirmedSetpointsInPlaceAndTheNextStepContinues()
    {
        var (hardware, state, _, coordinator) = await ActiveChargeAsync();
        var before = state.Current;

        hardware.MissReads = 1;
        Assert.Equal(JointBatteryStepOutcome.ReadMissTolerated, await coordinator.StepAsync(Command(), CancellationToken.None));
        Assert.Empty(hardware.Commands);
        Assert.Same(before, state.Current);

        Assert.Equal(JointBatteryStepOutcome.Executed, await coordinator.StepAsync(Command(), CancellationToken.None));
        Assert.Equal("Active", state.Current.Status);
        Assert.Equal(500, state.Current.VictronTargetWatts);
        Assert.DoesNotContain("Victron:0", hardware.Commands);
        Assert.Equal(0, state.ConsecutiveReadMisses);
    }

    [Fact]
    public async Task SecondConsecutiveMissedReadIsAFault()
    {
        var (hardware, _, _, coordinator) = await ActiveChargeAsync();
        hardware.MissReads = 2;
        await coordinator.StepAsync(Command(), CancellationToken.None);
        await Assert.ThrowsAsync<JointBatteryReadMissException>(() => coordinator.StepAsync(Command(), CancellationToken.None));
    }

    [Theory]
    [InlineData("aged")]
    [InlineData("expired")]
    [InlineData("direction")]
    [InlineData("hold")]
    [InlineData("starting")]
    public async Task MissedReadIsAFaultWheneverTheSetpointsInPlaceAreNotClearlyStillWanted(string situation)
    {
        var (hardware, state, clock, coordinator) = await ActiveChargeAsync();
        var command = Command();
        switch (situation)
        {
            // Older than one cycle budget: nobody has looked at the site for too long.
            case "aged": clock.UtcNow = Now.AddSeconds(6.1); break;
            // The command behind the setpoints is over (end of its quarter hour or manual charge).
            case "expired": clock.UtcNow = Now.AddSeconds(2); state.Set(state.Current with { ValidUntilUtc = Now.AddSeconds(1) }); break;
            // The new command wants the other direction or a stop: that must not wait for the next read.
            case "direction": command = Command(-500, -500); break;
            case "hold": command = Command(0, 0); break;
            // E3/DC start-up: its mode is not confirmed yet.
            case "starting": state.Set(state.Current with { Status = "Starting" }); break;
        }
        hardware.MissReads = 1;
        await Assert.ThrowsAsync<JointBatteryReadMissException>(() => coordinator.StepAsync(command, CancellationToken.None));
        Assert.Empty(hardware.Commands);
    }

    [Fact]
    public async Task MissedReadIsNeverToleratedInTransitionReleaseOrAfterAWrite()
    {
        // Neutral transition before the first action.
        var transition = new Hardware { MissReads = 1 };
        await Assert.ThrowsAsync<JointBatteryReadMissException>(() =>
            new JointBatteryExecutionCoordinator(transition, new(), new Clock()).StepAsync(Command(), CancellationToken.None));

        // Release reads after its stop commands.
        var (hardware, _, _, coordinator) = await ActiveChargeAsync();
        hardware.MissReads = 1;
        await Assert.ThrowsAsync<JointBatteryReadMissException>(() => coordinator.ReleaseAsync(CancellationToken.None));

        // The same signal from a write path, after the E3/DC has been commanded in this step.
        (hardware, _, _, coordinator) = await ActiveChargeAsync();
        hardware.FailVictronWrite = new JointBatteryReadMissException("Victron beantwortet Leseanfragen nicht: Testpfad.");
        await Assert.ThrowsAsync<JointBatteryReadMissException>(() => coordinator.StepAsync(Command(), CancellationToken.None));
        Assert.Contains(hardware.Commands, c => c.StartsWith("E3dc:ChargeGrid", StringComparison.Ordinal));
    }

    private static JointBatteryCommand Following(int a, int b) => Command(a, b) with { ChargeSource = null, FollowsDemand = true };

    /// <summary>Site with the given house load on L1; the grid meter sees the load plus what both batteries exchange.</summary>
    private static JointBatteryLiveSnapshot Site(int houseLoad, int primary = 0, int secondary = 0, decimal primarySoc = 50, decimal secondarySoc = 50) =>
        Live(primary, secondary) with
        {
            Batteries = [Unit("victron", primary, primarySoc), Unit("e3dc", secondary, secondarySoc)],
            PhaseGridWatts = [houseLoad + primary + secondary, 0, 0]
        };

    [Fact]
    public void DemandFollowingDischargeRisesToTheMeasuredDemandInThePlannedProportion()
    {
        // The plan saw 300 W; now the house draws 3 kW.
        var limited = JointBatteryLiveLimiter.Limit(Following(-200, -100), Site(3000), Now);

        Assert.InRange(-(limited.VictronWatts + limited.E3dcWatts), 2890, 2900);
        Assert.InRange(limited.VictronWatts, -1934, -1930);
        Assert.InRange(limited.E3dcWatts, -967, -965);
        // Without the mark the planned watts stay the upper bound, as before.
        var capped = JointBatteryLiveLimiter.Limit(Command(-200, -100) with { ChargeSource = null }, Site(3000), Now);
        Assert.Equal((-200, -100), (capped.VictronWatts, capped.E3dcWatts));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(250)]
    [InlineData(1500)]
    public void DemandBelowThePlanCutsAFollowingDischargeExactlyLikeAnyOther(int houseLoad)
    {
        var following = JointBatteryLiveLimiter.Limit(Following(-1000, -1000), Site(houseLoad), Now);
        var planned = JointBatteryLiveLimiter.Limit(Command(-1000, -1000) with { ChargeSource = null }, Site(houseLoad), Now);

        Assert.Equal((planned.VictronWatts, planned.E3dcWatts), (following.VictronWatts, following.E3dcWatts));
        Assert.InRange(-(following.VictronWatts + following.E3dcWatts), 0, Math.Max(0, houseLoad - 100));
    }

    [Fact]
    public void FollowingNeverExceedsDemandMinusMarginNorABatterysOwnLimit()
    {
        foreach (var shares in new[] { (-200, -100), (-1, -40), (-300, 0), (0, -300), (-2500, -2500) })
        for (var houseLoad = 0; houseLoad <= 9000; houseLoad += 137)
        {
            var limited = JointBatteryLiveLimiter.Limit(Following(shares.Item1, shares.Item2), Site(houseLoad), Now);

            Assert.True(limited.VictronWatts <= 0 && limited.E3dcWatts <= 0);
            Assert.True(-(limited.VictronWatts + limited.E3dcWatts) <= Math.Max(0, houseLoad - 100), $"Last {houseLoad}: {limited}");
            Assert.True(limited.VictronWatts >= -3000 && limited.E3dcWatts >= -3000, $"Last {houseLoad}: {limited}");
            // The plan's choice of batteries is kept.
            if (shares.Item1 == 0) Assert.Equal(0, limited.VictronWatts);
            if (shares.Item2 == 0) Assert.Equal(0, limited.E3dcWatts);
        }
    }

    [Fact]
    public void FollowingDoesNotMoveTheShortfallOfABatteryAtItsPowerLimitToTheOther()
    {
        // 9 kW demand, shares 5:1: the Victron stops at its 3 kW, the E3/DC keeps its share of the demand.
        var limited = JointBatteryLiveLimiter.Limit(Following(-500, -100), Site(9000), Now);

        Assert.Equal(-3000, limited.VictronWatts);
        Assert.InRange(limited.E3dcWatts, -1484, -1482);
    }

    [Fact]
    public void FollowingRespectsTheReserveAndTheActiveDeviceLimit()
    {
        // Victron at its reserve: no share, nothing to raise. The E3/DC alone follows.
        var atReserve = JointBatteryLiveLimiter.Limit(Following(-200, -100), Site(3000, primarySoc: 10), Now);
        Assert.Equal(0, atReserve.VictronWatts);
        Assert.InRange(atReserve.E3dcWatts, -2900, -2890);

        // 1 Wh above the reserve bounds the power (energy over the 10 s the allocator looks ahead).
        var nearlyEmpty = JointBatteryLiveLimiter.Limit(Following(-200, -100), Site(6000, primarySoc: 10.01m), Now);
        Assert.InRange(nearlyEmpty.VictronWatts, -400, 0);

        // The limit the E3/DC reports (or the operator's own E3/DC limit) stays the cap.
        var live = Site(6000);
        live = live with { Batteries = [live.Batteries[0], live.Batteries[1] with { AvailableDischargeWatts = 700 }] };
        var deviceLimited = JointBatteryLiveLimiter.Limit(Following(-200, -100), live, Now);
        Assert.Equal(-700, deviceLimited.E3dcWatts);
        Assert.True(deviceLimited.VictronWatts >= -3000);
    }

    [Fact]
    public void FeedInStopAndPvSurplusRulesAreUnchangedForAFollowingDischarge()
    {
        // Export while a battery discharges: stop, whatever the plan wants.
        var export = Live(-2000, 0) with { PhaseGridWatts = [-1000, 0, 0] };
        var stopped = JointBatteryLiveLimiter.Limit(Following(-500, -500), export, Now);
        Assert.Equal((0, 0), (stopped.VictronWatts, stopped.E3dcWatts));
        Assert.Contains("Netzeinspeisung gestoppt", stopped.Reason);

        // PV surplus is stored instead.
        var surplus = Live() with { PhaseGridWatts = [-1000, -1000, -1000] };
        var stored = JointBatteryLiveLimiter.Limit(Following(-500, -500), surplus, Now);
        Assert.Equal(BatteryChargeSource.PV, stored.ChargeSource);
        Assert.True(stored.VictronWatts > 0 && stored.E3dcWatts > 0);
    }

    [Fact]
    public void OpposingTargetsStayAFaultForAFollowingCommand()
    {
        var opposing = new JointBatteryCommand(-500, 500, null, Now.AddMinutes(1), "Testvorgabe") { FollowsDemand = true };
        Assert.Throws<InvalidOperationException>(() => JointBatteryLiveLimiter.Limit(opposing, Site(3000), Now));
    }

    private static (Hardware Hardware, JointBatteryExecutionState State, JointBatteryExecutionCoordinator Coordinator, int[] HouseLoad) DischargingAt(
        int victron, int e3dc, int houseLoad)
    {
        var load = new[] { houseLoad };
        var hardware = new Hardware { Primary = victron, Secondary = e3dc };
        hardware.Adjust = live => live with { PhaseGridWatts = [load[0] + hardware.Primary + hardware.Secondary, 0, 0] };
        var state = new JointBatteryExecutionState { NeutralSamples = 2, Direction = -1, SettlingSinceUtc = Now.AddSeconds(-100) };
        state.Set(new("Active", "", Now, true, victron, e3dc));
        return (hardware, state, new JointBatteryExecutionCoordinator(hardware, state, new Clock()), load);
    }

    [Fact]
    public async Task RisingLoadRaisesAFollowingDischargeByTheRampUntilTheDemandIsCovered()
    {
        // Plan and batteries at 300 W; the oven adds 2.7 kW.
        var (hardware, state, coordinator, load) = DischargingAt(-200, -100, houseLoad: 400);
        load[0] = 3000;

        await coordinator.StepAsync(Following(-200, -100), CancellationToken.None);
        Assert.Equal((-450, -350), (state.Current.VictronTargetWatts, state.Current.E3dcTargetWatts));
        await coordinator.StepAsync(Following(-200, -100), CancellationToken.None);
        Assert.Equal((-700, -600), (state.Current.VictronTargetWatts, state.Current.E3dcTargetWatts));

        for (var i = 0; i < 10; i++)
        {
            await coordinator.StepAsync(Following(-200, -100), CancellationToken.None);
            Assert.Equal("Active", state.Current.Status);
            // The batteries never deliver more than the house draws.
            Assert.True(load[0] + hardware.Primary + hardware.Secondary >= 0);
        }
        var delivered = -(state.Current.VictronTargetWatts + state.Current.E3dcTargetWatts);
        Assert.InRange(delivered, 2750, 2900);
        // Shares of the plan (2:1), no neutral transition on the way.
        Assert.InRange(state.Current.VictronTargetWatts, -1940, -1820);
        Assert.DoesNotContain("Victron:0", hardware.Commands);
        Assert.DoesNotContain("E3dc:Idle:0", hardware.Commands);
    }

    [Fact]
    public async Task WithoutTheMarkARisingLoadLeavesThePlannedWattsInPlace()
    {
        var (_, state, coordinator, load) = DischargingAt(-200, -100, houseLoad: 400);
        load[0] = 3000;

        for (var i = 0; i < 5; i++) await coordinator.StepAsync(Command(-200, -100) with { ChargeSource = null }, CancellationToken.None);

        Assert.Equal((-200, -100), (state.Current.VictronTargetWatts, state.Current.E3dcTargetWatts));
    }

    [Fact]
    public async Task FallingLoadCutsAFollowingDischargeAtOnceAndALargeDropStopsIt()
    {
        var (hardware, state, coordinator, load) = DischargingAt(-1900, -950, houseLoad: 3000);

        // 300 W less: the same step lowers both targets, no ramp downwards.
        load[0] = 2700;
        await coordinator.StepAsync(Following(-200, -100), CancellationToken.None);
        Assert.Equal("Active", state.Current.Status);
        Assert.InRange(-(state.Current.VictronTargetWatts + state.Current.E3dcTargetWatts), 2450, 2600);
        Assert.True(load[0] + hardware.Primary + hardware.Secondary >= 0);

        // The oven switches off: the batteries are exporting when the step measures, so the existing feed-in stop
        // takes both to neutral in this very step.
        load[0] = 300;
        hardware.Commands.Clear();
        await coordinator.StepAsync(Following(-200, -100), CancellationToken.None);
        Assert.Equal("Transition", state.Current.Status);
        Assert.Equal(["Victron:0", "E3dc:Idle:0"], hardware.Commands);
        Assert.Equal((0, 0), (hardware.Primary, hardware.Secondary));
    }

    [Fact]
    public async Task ABatteryThePlanLeftAtZeroStaysAtZeroWhileTheOtherFollows()
    {
        var (hardware, state, coordinator, load) = DischargingAt(0, -300, houseLoad: 400);
        load[0] = 5000;

        for (var i = 0; i < 15; i++)
        {
            await coordinator.StepAsync(Following(0, -300), CancellationToken.None);
            Assert.Equal(0, state.Current.VictronTargetWatts);
        }

        // The E3/DC stops at its own 3 kW; the remaining 2 kW stay on the grid until the next plan.
        Assert.Equal(-3000, state.Current.E3dcTargetWatts);
        Assert.DoesNotContain(hardware.Commands, c => c.StartsWith("Victron:", StringComparison.Ordinal) && c != "Victron:0");
    }

    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => Now; }
    private sealed class MovingClock : IUtcClock { public DateTimeOffset UtcNow { get; set; } = Now; }
    private sealed class Hardware : IJointBatteryHardware
    {
        internal readonly List<string> Commands = [];
        internal int Primary; internal int Secondary;
        internal bool BadEcho; internal bool DelayMode; internal bool Follow = true; internal bool FailE3dc;
        internal Func<JointBatteryLiveSnapshot, JointBatteryLiveSnapshot>? Adjust;
        internal int? Grant;
        internal int MissReads; internal Action? BeforeRead; internal Exception? FailVictronWrite;
        public Task<JointBatteryLiveSnapshot> ReadAsync(CancellationToken token)
        {
            BeforeRead?.Invoke();
            if (MissReads > 0) { MissReads--; throw new JointBatteryReadMissException("Victron beantwortet Leseanfragen nicht: Testpfad."); }
            var live = Live(Primary, Secondary);
            return Task.FromResult(Adjust is null ? live : Adjust(live));
        }
        public Task SetVictronAsync(int watts, CancellationToken token)
        {
            Commands.Add("Victron:" + watts);
            if (FailVictronWrite is not null) throw FailVictronWrite;
            if (Follow) Primary = watts; return Task.CompletedTask;
        }
        public Task<E3dcPowerAcknowledgement> SetE3dcAsync(E3dcPowerCommand command, CancellationToken token)
        {
            Commands.Add($"E3dc:{command.Mode}:{command.PowerWatts}");
            if (FailE3dc) throw new IOException("Simulierter Verbindungsabbruch");
            var granted = Grant is { } grant ? Math.Min(grant, command.PowerWatts) : command.PowerWatts;
            var signed = command.Mode == E3dcPowerMode.Discharge ? -granted : command.Mode is E3dcPowerMode.ChargeGrid or E3dcPowerMode.ChargePv ? granted : 0;
            if (Follow) Secondary = signed;
            // Real EMS_MODE scale: 0 idle, 1 discharge, 2 charge (grid and PV alike).
            var readback = command.Mode switch
            {
                E3dcPowerMode.ChargeGrid or E3dcPowerMode.ChargePv => 2,
                E3dcPowerMode.Discharge => 1,
                _ => 0
            };
            return Task.FromResult(new E3dcPowerAcknowledgement(BadEcho ? command.PowerWatts + 1000 : granted, DelayMode ? 0 : readback, Secondary, Now));
        }
    }
}
