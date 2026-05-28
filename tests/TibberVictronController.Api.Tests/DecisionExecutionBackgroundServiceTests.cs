using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

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
