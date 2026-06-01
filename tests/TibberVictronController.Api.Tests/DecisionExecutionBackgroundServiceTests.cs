using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Tests;

public sealed class DecisionExecutionBackgroundServiceTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 5, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteSingleCycleAsyncCalculatesDecisionAndUsesConfiguredInterval()
    {
        var decisionService = new RecordingCurrentBatteryDecisionService();
        var setpointPublisher = new RecordingVictronSetpointPublisher();
        var settingsStore = new InMemoryControllerSettingStore();
        await settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.DecisionWorkerIntervalSecondsKey,
            "90",
            ControllerSettingSensitivity.Normal,
            NowUtc));
        await settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.VictronDryRunKey,
            "true",
            ControllerSettingSensitivity.Normal,
            NowUtc));
        var serviceProvider = CreateServiceProvider(
            decisionService,
            settingsStore,
            new RecordingOperationalEventRepository(),
            new RecordingWorkerFailureNotifier(),
            setpointPublisher);
        var backgroundService = new DecisionExecutionBackgroundService(
            serviceProvider,
            NullLogger<DecisionExecutionBackgroundService>.Instance,
            new DecisionWorkerRuntimeStatus());

        var delay = await backgroundService.ExecuteSingleCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(90), delay);
        Assert.Equal(1, decisionService.CallCount);
        Assert.Empty(setpointPublisher.PublishedResults);
    }

    [Fact]
    public async Task ExecuteSingleCycleAsyncPublishesSetpointWhenDryRunIsDisabled()
    {
        var decisionService = new RecordingCurrentBatteryDecisionService(new CurrentBatteryDecision(
            new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.PV),
            targetPowerWatts: 1200));
        var setpointPublisher = new RecordingVictronSetpointPublisher();
        var settingsStore = new InMemoryControllerSettingStore();
        await settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.DecisionWorkerIntervalSecondsKey,
            "60",
            ControllerSettingSensitivity.Normal,
            NowUtc));
        await settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.VictronDryRunKey,
            "false",
            ControllerSettingSensitivity.Normal,
            NowUtc));
        var serviceProvider = CreateServiceProvider(
            decisionService,
            settingsStore,
            new RecordingOperationalEventRepository(),
            new RecordingWorkerFailureNotifier(),
            setpointPublisher);
        var backgroundService = new DecisionExecutionBackgroundService(
            serviceProvider,
            NullLogger<DecisionExecutionBackgroundService>.Instance,
            new DecisionWorkerRuntimeStatus());

        await backgroundService.ExecuteSingleCycleAsync(CancellationToken.None);

        Assert.Single(setpointPublisher.PublishedResults);
        Assert.Equal(1200, setpointPublisher.PublishedResults[0].Decision.TargetPowerWatts);
    }

    [Fact]
    public async Task ExecuteSingleCycleAsyncUsesFallbackIntervalWhenSettingIsInvalid()
    {
        var decisionService = new RecordingCurrentBatteryDecisionService();
        var settingsStore = new InMemoryControllerSettingStore();
        await settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.DecisionWorkerIntervalSecondsKey,
            "ungueltig",
            ControllerSettingSensitivity.Normal,
            NowUtc));
        var serviceProvider = CreateServiceProvider(
            decisionService,
            settingsStore,
            new RecordingOperationalEventRepository(),
            new RecordingWorkerFailureNotifier(),
            new RecordingVictronSetpointPublisher());
        var backgroundService = new DecisionExecutionBackgroundService(
            serviceProvider,
            NullLogger<DecisionExecutionBackgroundService>.Instance,
            new DecisionWorkerRuntimeStatus());

        var delay = await backgroundService.ExecuteSingleCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(60), delay);
        Assert.Equal(1, decisionService.CallCount);
    }

    [Fact]
    public async Task ExecuteSingleCycleAsyncCapsIntervalWhenExternalEssIsActive()
    {
        var decisionService = new RecordingCurrentBatteryDecisionService();
        var settingsStore = new InMemoryControllerSettingStore();
        await settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.DecisionWorkerIntervalSecondsKey,
            "90",
            ControllerSettingSensitivity.Normal,
            NowUtc));
        await settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.VictronControlModeKey,
            "externalEss",
            ControllerSettingSensitivity.Normal,
            NowUtc));
        var serviceProvider = CreateServiceProvider(
            decisionService,
            settingsStore,
            new RecordingOperationalEventRepository(),
            new RecordingWorkerFailureNotifier(),
            new RecordingVictronSetpointPublisher());
        var backgroundService = new DecisionExecutionBackgroundService(
            serviceProvider,
            NullLogger<DecisionExecutionBackgroundService>.Instance,
            new DecisionWorkerRuntimeStatus());

        var delay = await backgroundService.ExecuteSingleCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(45), delay);
    }

    [Fact]
    public async Task HandleCycleFailureAsyncSavesEventAndTriggersNotifier()
    {
        var serviceProvider = CreateServiceProvider(
            new RecordingCurrentBatteryDecisionService(),
            new InMemoryControllerSettingStore(),
            new RecordingOperationalEventRepository(),
            new RecordingWorkerFailureNotifier(),
            new RecordingVictronSetpointPublisher());
        var backgroundService = new DecisionExecutionBackgroundService(
            serviceProvider,
            NullLogger<DecisionExecutionBackgroundService>.Instance,
            new DecisionWorkerRuntimeStatus());
        var operationalEventRepository = serviceProvider.GetRequiredService<IOperationalEventRepository>() as RecordingOperationalEventRepository;
        var notifier = serviceProvider.GetRequiredService<IWorkerFailureNotifier>() as RecordingWorkerFailureNotifier;

        await backgroundService.HandleCycleFailureAsync(new InvalidOperationException("Testfehler"), CancellationToken.None);

        Assert.NotNull(operationalEventRepository);
        Assert.NotNull(notifier);
        Assert.Single(operationalEventRepository!.Events);
        Assert.Single(notifier!.SentExceptions);
    }

    [Fact]
    public void CalculateGridSetpointWattsStopsCurrentBatteryChargeWhenDecisionIsIdle()
    {
        var decisionResult = CreateDecisionResult(
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
                targetPowerWatts: 0),
            new CurrentSiteTelemetry(
                currentGridImportWatts: -2995,
                currentPvProductionWatts: 0,
                measuredAtUtc: NowUtc,
                currentBatteryPowerWatts: 2429));

        var gridSetpointWatts = MqttVictronSetpointPublisher.CalculateGridSetpointWatts(decisionResult);

        Assert.Equal(-5424, gridSetpointWatts);
    }

    [Fact]
    public void CalculateGridSetpointWattsKeepsTargetChargePowerRelativeToCurrentBatteryPower()
    {
        var decisionResult = CreateDecisionResult(
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.PV),
                targetPowerWatts: 2500),
            new CurrentSiteTelemetry(
                currentGridImportWatts: -2995,
                currentPvProductionWatts: 0,
                measuredAtUtc: NowUtc,
                currentBatteryPowerWatts: 2429));

        var gridSetpointWatts = MqttVictronSetpointPublisher.CalculateGridSetpointWatts(decisionResult);

        Assert.Equal(-2924, gridSetpointWatts);
    }

    [Fact]
    public void CalculateExternalEssSetpointWattsUsesDirectSignedBatteryTarget()
    {
        var chargeDecision = CreateDecisionResult(
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
                targetPowerWatts: 2500),
            new CurrentSiteTelemetry(-4000, 0, NowUtc, currentBatteryPowerWatts: 0));
        var dischargeDecision = CreateDecisionResult(
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Discharge, chargeSource: null),
                targetPowerWatts: 1200),
            new CurrentSiteTelemetry(1800, 0, NowUtc, currentBatteryPowerWatts: 0));

        Assert.Equal(2500, MqttVictronSetpointPublisher.CalculateExternalEssSetpointWatts(chargeDecision));
        Assert.Equal(-1200, MqttVictronSetpointPublisher.CalculateExternalEssSetpointWatts(dischargeDecision));
    }

    [Fact]
    public void CalculateVebusChargePowerLimitWattsKeepsSafetyBufferBelowCurrentLimit()
    {
        var limitWatts = MqttVictronSetpointPublisher.CalculateVebusChargePowerLimitWatts(
            maxChargeCurrentAmps: 50m,
            batteryVoltageVolts: 49.92m);

        Assert.Equal(2246, limitWatts);
    }

    [Fact]
    public void CalculateHub4ControlDisablesChargeAndFeedInInsideIdleThreshold()
    {
        var decisionResult = CreateDecisionResult(
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
                targetPowerWatts: 0),
            new CurrentSiteTelemetry(8, 0, NowUtc, currentBatteryPowerWatts: 40));

        var control = MqttVictronSetpointPublisher.CalculateHub4Control(decisionResult, batteryIdleThresholdWatts: 100);

        Assert.True(control.DisableCharge);
        Assert.True(control.DisableFeedIn);
    }

    [Fact]
    public void CalculateHub4ControlAllowsOnlyChargeWhenCharging()
    {
        var decisionResult = CreateDecisionResult(
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.PV),
                targetPowerWatts: 2500),
            new CurrentSiteTelemetry(-3000, 0, NowUtc, currentBatteryPowerWatts: 2400));

        var control = MqttVictronSetpointPublisher.CalculateHub4Control(decisionResult, batteryIdleThresholdWatts: 100);

        Assert.False(control.DisableCharge);
        Assert.True(control.DisableFeedIn);
    }

    [Fact]
    public void CalculateHub4ControlAllowsOnlyFeedInWhenDischarging()
    {
        var decisionResult = CreateDecisionResult(
            new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Discharge, chargeSource: null),
                targetPowerWatts: 1200),
            new CurrentSiteTelemetry(1200, 0, NowUtc, currentBatteryPowerWatts: 0));

        var control = MqttVictronSetpointPublisher.CalculateHub4Control(decisionResult, batteryIdleThresholdWatts: 100);

        Assert.True(control.DisableCharge);
        Assert.False(control.DisableFeedIn);
    }

    [Theory]
    [InlineData(1500, 3, new[] { 500, 500, 500 })]
    [InlineData(1501, 3, new[] { 501, 500, 500 })]
    [InlineData(-1501, 3, new[] { -501, -500, -500 })]
    public void SplitSetpointAcrossPhasesPreservesTotalSetpoint(int totalSetpointWatts, int phaseCount, int[] expectedSetpoints)
    {
        var setpoints = MqttVictronSetpointPublisher.SplitSetpointAcrossPhases(totalSetpointWatts, phaseCount);

        Assert.Equal(expectedSetpoints, setpoints);
        Assert.Equal(totalSetpointWatts, setpoints.Sum());
    }

    [Theory]
    [InlineData(VictronControlMode.NormalEss, 1)]
    [InlineData(VictronControlMode.ExternalEss, 3)]
    public void CalculateHub4ModeValueMapsControlModeToVictronMode(VictronControlMode controlMode, int expectedValue)
    {
        Assert.Equal(expectedValue, MqttVictronSetpointPublisher.CalculateHub4ModeValue(controlMode));
    }

    [Theory]
    [InlineData(false, 0, 3, false)]
    [InlineData(true, 3, 3, false)]
    [InlineData(true, 1, 3, true)]
    public void ShouldPublishHub4ModeOnlyWritesKnownMismatches(
        bool hasCurrentMode,
        decimal currentMode,
        int desiredMode,
        bool expectedResult)
    {
        Assert.Equal(expectedResult, MqttVictronSetpointPublisher.ShouldPublishHub4Mode(hasCurrentMode, currentMode, desiredMode));
    }

    private static ServiceProvider CreateServiceProvider(
        ICurrentBatteryDecisionService currentBatteryDecisionService,
        IControllerSettingStore controllerSettingStore,
        IOperationalEventRepository operationalEventRepository,
        IWorkerFailureNotifier workerFailureNotifier,
        IVictronSetpointPublisher setpointPublisher)
    {
        var services = new ServiceCollection();
        services.AddSingleton(currentBatteryDecisionService);
        services.AddSingleton(controllerSettingStore);
        services.AddSingleton(operationalEventRepository);
        services.AddSingleton(workerFailureNotifier);
        services.AddSingleton(setpointPublisher);
        services.AddSingleton<IUtcClock>(new FixedUtcClock(NowUtc));

        return services.BuildServiceProvider();
    }

    private static CurrentBatteryDecisionResult CreateDecisionResult(
        CurrentBatteryDecision decision,
        CurrentSiteTelemetry siteTelemetry)
    {
        return new CurrentBatteryDecisionResult(
            decidedAtUtc: NowUtc,
            validFromUtc: NowUtc,
            validToUtc: NowUtc.AddMinutes(15),
            decision,
            batteryState: new BatteryState(50m, NowUtc),
            siteTelemetry,
            tibberPricePerKwh: null,
            tibberPriceCurrency: null,
            reasons: new[] { new BatteryDecisionReason("TEST", "Testbegruendung") },
            inputSummaryJson: "{}");
    }

    private sealed class RecordingCurrentBatteryDecisionService : ICurrentBatteryDecisionService
    {
        private readonly CurrentBatteryDecision decision;

        public RecordingCurrentBatteryDecisionService()
            : this(new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
                targetPowerWatts: 0))
        {
        }

        public RecordingCurrentBatteryDecisionService(CurrentBatteryDecision decision)
        {
            this.decision = decision;
        }

        public int CallCount { get; private set; }

        public Task<CurrentBatteryDecisionResult> CalculateCurrentDecisionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            return Task.FromResult(new CurrentBatteryDecisionResult(
                decidedAtUtc: NowUtc,
                validFromUtc: NowUtc,
                validToUtc: NowUtc.AddMinutes(15),
                decision: decision,
                batteryState: new BatteryState(50m, NowUtc),
                siteTelemetry: new CurrentSiteTelemetry(0, 0, NowUtc),
                tibberPricePerKwh: null,
                tibberPriceCurrency: null,
                reasons: new[] { new BatteryDecisionReason("TEST", "Testbegruendung") },
                inputSummaryJson: "{}"));
        }
    }

    private sealed class RecordingVictronSetpointPublisher : IVictronSetpointPublisher
    {
        public List<CurrentBatteryDecisionResult> PublishedResults { get; } = new();

        public Task PublishAsync(CurrentBatteryDecisionResult decisionResult, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishedResults.Add(decisionResult);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOperationalEventRepository : IOperationalEventRepository
    {
        public List<OperationalEvent> Events { get; } = new();

        public Task SaveEventAsync(OperationalEvent operationalEvent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(operationalEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OperationalEvent>> GetRecentEventsAsync(int maxCount, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<OperationalEvent>>(Events.Take(maxCount).ToArray());
        }
    }

    private sealed class RecordingWorkerFailureNotifier : IWorkerFailureNotifier
    {
        public List<Exception> SentExceptions { get; } = new();

        public Task NotifyAsync(Exception exception, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SentExceptions.Add(exception);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryControllerSettingStore : IControllerSettingStore
    {
        private readonly Dictionary<string, ControllerSetting> settingsByKey = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ControllerSetting>>(settingsByKey.Values.ToArray());
        }

        public Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            settingsByKey.TryGetValue(key, out var setting);
            return Task.FromResult(setting);
        }

        public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            settingsByKey[setting.Key] = setting;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedUtcClock : IUtcClock
    {
        public FixedUtcClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
