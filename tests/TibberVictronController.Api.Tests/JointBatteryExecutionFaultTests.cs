using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Api.JointBattery;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

/// <summary>
/// What the execution loop treats as a fault: with a clock that moves and a fake <see cref="IJointBatteryHardware"/>.
/// No test here touches a device.
/// </summary>
public sealed class JointBatteryExecutionFaultTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task QuarterHourEndingWhileTheDevicesAreReadIsNoFault()
    {
        // Field data: every stop with "Gemeinsamer Sollwert oder Messwerte sind abgelaufen." was logged at exactly
        // hh:00, hh:15, hh:30 or hh:45. The command was selected just before the slot end and limited just after it.
        var rig = await Rig.CreateAsync(plan: false);
        rig.State.CommittedSlot = new(new ForecastTimeSlot(Start.AddMinutes(-15).AddMilliseconds(200), Start.AddMilliseconds(200)), 0.18m, "EUR",
            false, BatteryChargeSource.Grid, [new("victron", 1000, 50, 52), new("e3dc", 500, 50, 51)], "Festgeschriebene Testladung", false);
        rig.Hardware.ReadDuration = TimeSpan.FromMilliseconds(400);

        await rig.Worker.TickAsync(CancellationToken.None);

        Assert.Equal("Transition", rig.State.Current.Status);
        Assert.Null(rig.State.BlockedUntilUtc);

        // The next tick lies in the new quarter hour and selects for it (here: no plan, so self-consumption).
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.Worker.TickAsync(CancellationToken.None);
        Assert.Contains(rig.Events.Saved, e => e.Category == "JointBatteryFallback");
    }

    [Fact]
    public async Task PersistentFaultWritesOneErrorEventPerCauseAndInterval()
    {
        var rig = await Rig.CreateAsync(plan: false);
        rig.Hardware.ReadFailure = watts => new InvalidOperationException($"Testfehler bei {watts} W.");

        // 300 failing iterations within five minutes: the release cannot be confirmed, so every second fails again.
        for (var i = 0; i < 300; i++)
        {
            await rig.Worker.RunOnceAsync(CancellationToken.None);
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal("Restoring", rig.State.Current.Status);
        Assert.Single(Errors(rig));
        // Stopping is not throttled, only the event rows: every iteration still tried to neutralise.
        Assert.True(rig.Hardware.Commands.Count(c => c == "Victron:0") >= 300);

        // After the interval the same cause is written once more and names the repeats left out.
        await rig.Worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, Errors(rig).Count);
        Assert.Contains("\"SuppressedRepeats\":299", Errors(rig)[1].Details);
        Assert.Contains("299 gleichartige", Errors(rig)[1].Message);

        // A different cause is written at once.
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Hardware.ReadFailure = _ => new InvalidOperationException("Anderer Testfehler.");
        await rig.Worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(3, Errors(rig).Count);
        Assert.Contains("Anderer Testfehler.", Errors(rig)[2].Message);
    }

    [Fact]
    public async Task OneMissedReadKeepsTheConfirmedSetpointsAndControlContinuesWithoutNeutralTransition()
    {
        var rig = await Rig.CreateAsync();
        await rig.RunUntilActiveAsync();
        var before = rig.State.Current;
        rig.Hardware.Commands.Clear();

        rig.Hardware.MissReads = 1;
        await rig.RunAsync();

        // Nothing written, nothing stopped, no restart block; the skipped tick is visible as a warning.
        Assert.Empty(rig.Hardware.Commands);
        Assert.Same(before, rig.State.Current);
        Assert.Null(rig.State.BlockedUntilUtc);
        Assert.Empty(Errors(rig));
        Assert.Contains(rig.Events.Saved, e => e is { Category: "JointBatteryExecution", Severity: "Warning" });

        await rig.RunAsync();

        // Control continues from the confirmed targets: the next ramp step, no neutral command in between.
        Assert.Equal("Active", rig.State.Current.Status);
        Assert.Equal(before.VictronTargetWatts + 250, rig.State.Current.VictronTargetWatts);
        Assert.DoesNotContain("Victron:0", rig.Hardware.Commands);
        Assert.DoesNotContain(rig.Hardware.Commands, c => c.StartsWith("E3dc:Idle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondConsecutiveMissedReadStopsExactlyAsBefore()
    {
        var rig = await Rig.CreateAsync();
        await rig.RunUntilActiveAsync();
        rig.Hardware.Commands.Clear();

        rig.Hardware.MissReads = 2;
        await rig.RunAsync();
        Assert.Empty(rig.Hardware.Commands);
        await rig.RunAsync();

        AssertStopped(rig);
    }

    [Theory]
    // A read miss reported by a write path: the E3/DC has already been commanded in this tick.
    [InlineData(true)]
    // The Victron does not confirm the written setpoint.
    [InlineData(false)]
    public async Task FailureAfterAWriteStopsImmediately(bool readMissAfterWrite)
    {
        var rig = await Rig.CreateAsync();
        await rig.RunUntilActiveAsync();
        rig.Hardware.Commands.Clear();

        rig.Hardware.VictronWriteFailure = readMissAfterWrite
            ? new JointBatteryReadMissException("Victron beantwortet Leseanfragen nicht: Testpfad.")
            : new InvalidOperationException("Victron bestaetigt die gemeinsamen Sollwerte nicht: Testpfad soll 1586, ist 1582.");
        await rig.RunAsync();

        AssertStopped(rig);
    }

    [Fact]
    public async Task MissedReadDuringTheNeutralTransitionStopsImmediately()
    {
        var rig = await Rig.CreateAsync();
        await rig.RunAsync();
        Assert.Equal("Transition", rig.State.Current.Status);

        rig.Hardware.MissReads = 1;
        await rig.RunAsync();

        AssertStopped(rig);
    }

    [Fact]
    public void ThrottleWritesFirstOccurrenceThenOncePerIntervalWithTheSuppressedCount()
    {
        var throttle = new RepeatedEventThrottle(TimeSpan.FromMinutes(5), capacity: 2);
        Assert.True(throttle.ShouldWrite("a", Start, out var suppressed));
        Assert.Equal(0, suppressed);
        Assert.False(throttle.ShouldWrite("a", Start.AddSeconds(1), out _));
        Assert.False(throttle.ShouldWrite("a", Start.AddMinutes(4), out _));
        Assert.True(throttle.ShouldWrite("b", Start.AddMinutes(4), out _));
        Assert.True(throttle.ShouldWrite("a", Start.AddMinutes(5), out suppressed));
        Assert.Equal(2, suppressed);
        // Bounded: a third cause displaces the one written longest ago ("b"), which is then new again.
        Assert.True(throttle.ShouldWrite("c", Start.AddMinutes(6), out _));
        Assert.True(throttle.ShouldWrite("b", Start.AddMinutes(6), out suppressed));
        Assert.Equal(0, suppressed);
        Assert.Equal("Soll # W, gemessen # W", RepeatedEventThrottle.WithoutNumbers("Soll 250 W, gemessen -4318 W"));
    }

    private static void AssertStopped(Rig rig)
    {
        // Today's stop: Victron first, then E3/DC, hand-back to automatic once neutral, 30 s restart block, error event.
        Assert.Equal("Blocked", rig.State.Current.Status);
        Assert.Contains("erneuter Versuch fruehestens nach 30 Sekunden", rig.State.Current.Message);
        Assert.Equal(rig.Clock.UtcNow.AddSeconds(30), rig.State.BlockedUntilUtc);
        Assert.True(rig.Hardware.Commands.IndexOf("Victron:0") >= 0);
        Assert.True(rig.Hardware.Commands.LastIndexOf("Victron:0") < rig.Hardware.Commands.IndexOf("E3dc:Automatic:0"));
        Assert.Single(Errors(rig));
    }

    private static List<OperationalEvent> Errors(Rig rig) => rig.Events.Saved.Where(e => e.Severity == "Error").ToList();

    private sealed class Rig
    {
        internal required MovingClock Clock { get; init; }
        internal required FakeHardware Hardware { get; init; }
        internal required JointBatteryExecutionState State { get; init; }
        internal required Events Events { get; init; }
        internal required JointBatteryExecutionBackgroundService Worker { get; init; }

        internal static async Task<Rig> CreateAsync(bool plan = true)
        {
            var clock = new MovingClock();
            var settings = new Settings();
            foreach (var (key, value) in new[] {
                (ControllerSettingDefaults.JointBatteryExecutionEnabledKey, "true"), (ControllerSettingDefaults.VictronDryRunKey, "false"),
                (ControllerSettingDefaults.E3dcRscpEnabledKey, "true"), (ControllerSettingDefaults.E3dcRscpHostKey, "192.0.2.10"),
                (ControllerSettingDefaults.E3dcRscpPortKey, "5033"), (ControllerSettingDefaults.E3dcRscpUserKey, "test-user"),
                (ControllerSettingDefaults.E3dcRscpPasswordKey, "test-password"), (ControllerSettingDefaults.E3dcRscpKeyKey, "test-key"),
                (ControllerSettingDefaults.VictronControlModeKey, "externalEss"), (ControllerSettingDefaults.JointBatteryPhase1LimitKey, "14720"),
                (ControllerSettingDefaults.JointBatteryPhase2LimitKey, "14720"), (ControllerSettingDefaults.JointBatteryPhase3LimitKey, "14720"),
                (ControllerSettingDefaults.JointBatteryVictronInputLimitKey, "14720") })
                await settings.SaveSettingAsync(new(key, value, ControllerSettingSensitivity.Normal, Start));
            var runtime = new JointBatteryRuntimeState();
            if (plan)
            {
                var slot = new JointBatterySlot(new ForecastTimeSlot(Start.AddMinutes(-5), Start.AddMinutes(10)), 0.20m, "EUR", false,
                    BatteryChargeSource.Grid, [new("victron", 2000, 50, 52), new("e3dc", 2000, 50, 51)], "Testplan", false);
                runtime.Set(new("Shadow", "Testplan", Start, null, new JointBatteryPlan(Start, [], [slot], 0, [])));
            }
            var mqtt = new TibberVictronController.Dal.Mqtt.MqttTelemetrySnapshotStore();
            mqtt.UpdateBatterySoc(50m, Start); mqtt.UpdateGridPower(500m, Start); mqtt.UpdateBatteryPower(0m, Start);
            var state = new JointBatteryExecutionState();
            var events = new Events();
            var hardware = new FakeHardware(clock);
            // Kept alive for the test's lifetime; it owns no unmanaged resources.
            var services = new ServiceCollection().AddSingleton<IControllerSettingStore>(settings).AddSingleton<IUtcClock>(clock)
                .AddSingleton<IOperationalEventRepository>(events).AddSingleton(runtime).AddSingleton(state).AddSingleton(mqtt)
                .AddSingleton<IJointBatteryHardware>(hardware).AddScoped<JointBatteryExecutionCoordinator>()
                // Reaching "Active" projects the executed target into the Victron decision log.
                .AddScoped<ICurrentBatteryDecisionService>(provider => new JointAwareBatteryDecisionService(provider))
                .AddSingleton<IDecisionLogRepository>(new DecisionLog()).BuildServiceProvider();
            var worker = new JointBatteryExecutionBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), state,
                new VictronSetpointRefreshState(), NullLogger<JointBatteryExecutionBackgroundService>.Instance, new Watchdog());
            return new Rig { Clock = clock, Hardware = hardware, State = state, Events = events, Worker = worker };
        }

        /// <summary>One loop iteration one second after the previous one, like the service loop.</summary>
        internal async Task RunAsync()
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            await Worker.RunOnceAsync(CancellationToken.None);
        }

        internal async Task RunUntilActiveAsync()
        {
            // Two neutral observations, then the first ramp step.
            for (var i = 0; i < 3; i++) await RunAsync();
            Assert.Equal("Active", State.Current.Status);
            Assert.Equal(250, State.Current.VictronTargetWatts);
        }
    }

    private sealed class MovingClock : IUtcClock
    {
        public DateTimeOffset UtcNow { get; private set; } = Start;
        internal void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>Both batteries follow their commands at once; measurements carry the clock's current time.</summary>
    private sealed class FakeHardware(MovingClock clock) : IJointBatteryHardware
    {
        private int victron;
        private int e3dc;
        private int failures;
        internal List<string> Commands { get; } = [];
        internal TimeSpan ReadDuration { get; set; }
        internal int MissReads { get; set; }
        internal Func<int, Exception>? ReadFailure { get; set; }
        internal Exception? VictronWriteFailure { get; set; }

        public Task<JointBatteryLiveSnapshot> ReadAsync(CancellationToken token)
        {
            clock.Advance(ReadDuration);
            if (ReadFailure is not null) throw ReadFailure(++failures);
            if (MissReads > 0)
            {
                MissReads--;
                throw new JointBatteryReadMissException("Victron beantwortet Leseanfragen nicht: Testpfad.");
            }
            var now = clock.UtcNow;
            JointBatteryUnit Unit(string id, int power) => new(id, new BatteryConfiguration(new BatteryConfigurationValues {
                TotalCapacityKwh = 10, MaximumChargePowerWatts = 3000, MaximumDischargePowerWatts = 3000, MinimumStateOfChargePercent = 10,
                PlanningMinimumStateOfChargePercent = 10, PlanningMaximumStateOfChargePercent = 95 }), new(50, now), power);
            return Task.FromResult(new JointBatteryLiveSnapshot(now, [Unit("victron", victron), Unit("e3dc", e3dc)],
                [1000 + victron + e3dc / 3, 1000 + e3dc / 3, 1000 + e3dc / 3], [200], victron, e3dc > 0 ? 2 : e3dc < 0 ? 1 : 0,
                new([10000, 10000, 10000], 5000, 1, 0)));
        }

        public Task SetVictronAsync(int watts, CancellationToken token)
        {
            Commands.Add("Victron:" + watts);
            // The failure belongs to an energising write; the stop that follows it must still get through.
            if (watts != 0 && VictronWriteFailure is { } failure) throw failure;
            victron = watts;
            return Task.CompletedTask;
        }

        public Task<E3dcPowerAcknowledgement> SetE3dcAsync(E3dcPowerCommand command, CancellationToken token)
        {
            Commands.Add($"E3dc:{command.Mode}:{command.PowerWatts}");
            e3dc = command.Mode == E3dcPowerMode.Discharge ? -command.PowerWatts :
                command.Mode is E3dcPowerMode.ChargeGrid or E3dcPowerMode.ChargePv ? command.PowerWatts : 0;
            // EMS_MODE scale: 0 idle, 1 discharge, 2 charge.
            return Task.FromResult(new E3dcPowerAcknowledgement(command.PowerWatts, e3dc > 0 ? 2 : e3dc < 0 ? 1 : 0, e3dc, clock.UtcNow));
        }
    }

    private sealed class Events : IOperationalEventRepository
    {
        internal List<OperationalEvent> Saved { get; } = [];
        public Task SaveEventAsync(OperationalEvent operationalEvent, CancellationToken cancellationToken = default)
        { Saved.Add(operationalEvent); return Task.CompletedTask; }
        public Task<IReadOnlyList<OperationalEvent>> GetRecentEventsAsync(int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OperationalEvent>>(Saved);
    }

    private sealed class DecisionLog : IDecisionLogRepository
    {
        public Task SaveDecisionAsync(DecisionLogEntry decisionLogEntry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DecisionLogEntry>> GetRecentDecisionsAsync(int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DecisionLogEntry>>([]);
        public Task<IReadOnlyList<DecisionLogEntry>> GetDecisionsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxCount,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DecisionLogEntry>>([]);
        public Task<int> DeleteDecisionsOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class Watchdog : IJointBatteryWatchdog
    {
        public bool IsSupervised => true;
        public Task PulseAsync(CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Settings : IControllerSettingStore
    {
        private readonly Dictionary<string, ControllerSetting> values = [];
        public Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.GetValueOrDefault(key));
        public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ControllerSetting>>(values.Values.ToArray());
        public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default)
        { values[setting.Key] = setting; return Task.CompletedTask; }
    }
}
