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
            new RecordingWorkerFailureNotifier());
        var backgroundService = new DecisionExecutionBackgroundService(serviceProvider, NullLogger<DecisionExecutionBackgroundService>.Instance);

        var delay = await backgroundService.ExecuteSingleCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(90), delay);
        Assert.Equal(1, decisionService.CallCount);
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
            new RecordingWorkerFailureNotifier());
        var backgroundService = new DecisionExecutionBackgroundService(serviceProvider, NullLogger<DecisionExecutionBackgroundService>.Instance);

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
            new RecordingWorkerFailureNotifier());
        var backgroundService = new DecisionExecutionBackgroundService(serviceProvider, NullLogger<DecisionExecutionBackgroundService>.Instance);
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
        IWorkerFailureNotifier workerFailureNotifier)
    {
        var services = new ServiceCollection();
        services.AddSingleton(currentBatteryDecisionService);
        services.AddSingleton(controllerSettingStore);
        services.AddSingleton(operationalEventRepository);
        services.AddSingleton(workerFailureNotifier);
        services.AddSingleton<IUtcClock>(new FixedUtcClock(NowUtc));

        return services.BuildServiceProvider();
    }

    private sealed class RecordingCurrentBatteryDecisionService : ICurrentBatteryDecisionService
    {
        public int CallCount { get; private set; }

        public Task<CurrentBatteryDecisionResult> CalculateCurrentDecisionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            return Task.FromResult(new CurrentBatteryDecisionResult(
                decidedAtUtc: NowUtc,
                validFromUtc: NowUtc,
                validToUtc: NowUtc.AddMinutes(15),
                decision: new CurrentBatteryDecision(
                    new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
                    targetPowerWatts: 0),
                batteryState: new BatteryState(50m, NowUtc),
                siteTelemetry: new CurrentSiteTelemetry(0, 0, NowUtc),
                tibberPricePerKwh: null,
                tibberPriceCurrency: null,
                reasons: new[] { new BatteryDecisionReason("TEST", "Testbegruendung") },
                inputSummaryJson: "{}"));
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
