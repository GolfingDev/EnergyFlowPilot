using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Api.JointBattery;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

public sealed class CachingControllerSettingStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private const string NormalKey = ControllerSettingDefaults.JointBatteryPhase1LimitKey;

    [Fact]
    public async Task WithoutTheScopeSwitchEveryReadGoesToTheDatabase()
    {
        var inner = new CountingStore();
        var store = new CachingControllerSettingStore(inner, new ScopedSettingReadCache());

        await store.GetSettingAsync(NormalKey);
        await store.GetSettingAsync(NormalKey);

        Assert.Equal(2, inner.Reads(NormalKey));
    }

    [Fact]
    public async Task EnabledCacheReadsASettingOnceButDeviceSwitchesEveryTime()
    {
        var inner = new CountingStore();
        await inner.SaveSettingAsync(new(NormalKey, "14720", ControllerSettingSensitivity.Normal, Now));
        var cache = new ScopedSettingReadCache();
        cache.Enable();
        var store = new CachingControllerSettingStore(inner, cache);

        Assert.Equal("14720", (await store.GetSettingAsync(NormalKey))?.Value);
        Assert.Equal("14720", (await store.GetSettingAsync(NormalKey))?.Value);
        Assert.Null(await store.GetSettingAsync("missing.key"));
        Assert.Null(await store.GetSettingAsync("missing.key"));
        foreach (var key in new[] { ControllerSettingDefaults.JointBatteryExecutionEnabledKey,
            ControllerSettingDefaults.JointBatteryRecoveryPendingKey, ControllerSettingDefaults.VictronDryRunKey })
        {
            await store.GetSettingAsync(key);
            await store.GetSettingAsync(key);
            Assert.Equal(2, inner.Reads(key));
        }

        Assert.Equal(1, inner.Reads(NormalKey));
        Assert.Equal(1, inner.Reads("missing.key"));
    }

    [Fact]
    public async Task SaveDropsTheCachedValue()
    {
        var inner = new CountingStore();
        await inner.SaveSettingAsync(new(NormalKey, "1000", ControllerSettingSensitivity.Normal, Now));
        var cache = new ScopedSettingReadCache();
        cache.Enable();
        var store = new CachingControllerSettingStore(inner, cache);
        await store.GetSettingAsync(NormalKey);

        await store.SaveSettingAsync(new(NormalKey, "2000", ControllerSettingSensitivity.Normal, Now));

        Assert.Equal("2000", (await store.GetSettingAsync(NormalKey))?.Value);
    }

    [Fact]
    public async Task JointTickReadsEachSettingOnceAcrossPreconditionsAndHardware()
    {
        // The start conditions read the phase limits, and the hardware adapter reads them again for the live limits.
        var inner = new CountingStore();
        foreach (var (key, value) in new[] {
            (ControllerSettingDefaults.JointBatteryExecutionEnabledKey, "true"), (ControllerSettingDefaults.VictronDryRunKey, "false"),
            (ControllerSettingDefaults.E3dcRscpEnabledKey, "true"), (ControllerSettingDefaults.E3dcRscpHostKey, "192.0.2.10"),
            (ControllerSettingDefaults.E3dcRscpPortKey, "5033"), (ControllerSettingDefaults.E3dcRscpUserKey, "test-user"),
            (ControllerSettingDefaults.E3dcRscpPasswordKey, "test-password"), (ControllerSettingDefaults.E3dcRscpKeyKey, "test-key"),
            (ControllerSettingDefaults.VictronControlModeKey, "externalEss"), (ControllerSettingDefaults.JointBatteryPhase1LimitKey, "14720"),
            (ControllerSettingDefaults.JointBatteryPhase2LimitKey, "14720"), (ControllerSettingDefaults.JointBatteryPhase3LimitKey, "14720"),
            (ControllerSettingDefaults.JointBatteryVictronInputLimitKey, "14720") })
            await inner.SaveSettingAsync(new(key, value, ControllerSettingSensitivity.Normal, Now));
        var state = new JointBatteryExecutionState();
        using var services = new ServiceCollection()
            .AddScoped<ScopedSettingReadCache>()
            .AddScoped<IControllerSettingStore>(provider => new CachingControllerSettingStore(inner, provider.GetRequiredService<ScopedSettingReadCache>()))
            .AddSingleton<IUtcClock>(new Clock()).AddSingleton(state).AddSingleton<IOperationalEventRepository>(new Events())
            .AddScoped<IJointBatteryHardware>(provider => new SettingReadingHardware(provider.GetRequiredService<IControllerSettingStore>()))
            .AddScoped<JointBatteryExecutionCoordinator>().BuildServiceProvider();
        var worker = new JointBatteryExecutionBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), state,
            new VictronSetpointRefreshState(), NullLogger<JointBatteryExecutionBackgroundService>.Instance, new Watchdog());

        await worker.TickAsync(CancellationToken.None);
        Assert.Equal(1, inner.Reads(NormalKey));
        Assert.True(state.Current.OwnsControl);

        // The next tick is a new scope and reads again, so changed settings take effect at the latest one tick later.
        await worker.TickAsync(CancellationToken.None);
        Assert.Equal(2, inner.Reads(NormalKey));
    }

    private sealed class CountingStore : IControllerSettingStore
    {
        private readonly Dictionary<string, ControllerSetting> values = [];
        private readonly Dictionary<string, int> reads = [];
        internal int Reads(string key) => reads.GetValueOrDefault(key);

        public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ControllerSetting>>(values.Values.ToArray());

        public Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            reads[key] = Reads(key) + 1;
            return Task.FromResult(values.GetValueOrDefault(key));
        }

        public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default)
        {
            values[setting.Key] = setting;
            return Task.CompletedTask;
        }
    }

    /// <summary>Reads a phase limit like <see cref="JointBatteryHardware"/>; measures neutral batteries.</summary>
    private sealed class SettingReadingHardware(IControllerSettingStore settings) : IJointBatteryHardware
    {
        public Task SetVictronAsync(int watts, CancellationToken token) => Task.CompletedTask;

        public Task<E3dcPowerAcknowledgement> SetE3dcAsync(E3dcPowerCommand command, CancellationToken token) =>
            Task.FromResult(new E3dcPowerAcknowledgement(0, 0, 0, Now));

        public async Task<JointBatteryLiveSnapshot> ReadAsync(CancellationToken token)
        {
            var limit = int.Parse((await settings.GetSettingAsync(NormalKey, token))!.Value!);
            return new JointBatteryLiveSnapshot(Now,
                [new("victron", new BatteryConfiguration(new BatteryConfigurationValues { TotalCapacityKwh = 10 }), new(50, Now), 0),
                 new("e3dc", new BatteryConfiguration(new BatteryConfigurationValues { TotalCapacityKwh = 10 }), new(50, Now), 0)],
                [0, 0, 0], [0], 0, 0, new([limit, limit, limit], 5000, 1, 0));
        }
    }

    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => Now; }

    private sealed class Watchdog : IJointBatteryWatchdog
    {
        public bool IsSupervised => true;
        public Task PulseAsync(CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Events : IOperationalEventRepository
    {
        public Task SaveEventAsync(OperationalEvent operationalEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<OperationalEvent>> GetRecentEventsAsync(int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OperationalEvent>>([]);
    }
}
