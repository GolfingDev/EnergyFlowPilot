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
    public async Task WrongE3dcEchoPreventsVictronStart()
    {
        var hardware = new Hardware(); var state = new JointBatteryExecutionState { NeutralSamples = 2, Direction = 1 };
        hardware.BadEcho = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JointBatteryExecutionCoordinator(hardware, state, new Clock()).StepAsync(Command(), CancellationToken.None));
        Assert.DoesNotContain(hardware.Commands, c => c == "Victron:250");
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

    [Fact]
    public void ExpiredCommandFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => JointBatteryLiveLimiter.Limit(Command() with { ValidUntilUtc = Now }, Live(), Now));
    }

    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => Now; }
    private sealed class Hardware : IJointBatteryHardware
    {
        internal readonly List<string> Commands = [];
        internal int Primary; internal int Secondary;
        internal bool BadEcho; internal bool DelayMode; internal bool Follow = true; internal bool FailE3dc;
        public Task<JointBatteryLiveSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(Live(Primary, Secondary));
        public Task SetVictronAsync(int watts, CancellationToken token) { Commands.Add("Victron:" + watts); if (Follow) Primary = watts; return Task.CompletedTask; }
        public Task<E3dcPowerAcknowledgement> SetE3dcAsync(E3dcPowerCommand command, CancellationToken token)
        {
            Commands.Add($"E3dc:{command.Mode}:{command.PowerWatts}");
            if (FailE3dc) throw new IOException("Simulierter Verbindungsabbruch");
            var signed = command.Mode == E3dcPowerMode.Discharge ? -command.PowerWatts : command.Mode is E3dcPowerMode.ChargeGrid or E3dcPowerMode.ChargePv ? command.PowerWatts : 0;
            if (Follow) Secondary = signed;
            return Task.FromResult(new E3dcPowerAcknowledgement(BadEcho ? 97 : command.PowerWatts,
                DelayMode ? 0 : command.Mode == E3dcPowerMode.ChargeGrid ? 2 : command.Mode is E3dcPowerMode.Idle or E3dcPowerMode.Automatic ? 0 : 1, Secondary, Now));
        }
    }
}
