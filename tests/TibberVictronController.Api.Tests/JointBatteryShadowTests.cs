using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Api.JointBattery;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Decisions;

namespace TibberVictronController.Api.Tests;

public sealed class JointBatteryShadowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 17, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ControllerSettingDefaults.E3dcRscpUserKey)]
    [InlineData(ControllerSettingDefaults.E3dcRscpPasswordKey)]
    [InlineData(ControllerSettingDefaults.E3dcRscpKeyKey)]
    public async Task RscpCredentialsAreEncryptedAndRemainHiddenInReadContracts(string key)
    {
        var inner = new Settings();
        var protector = new EphemeralDataProtectionProvider();
        var store = new ProtectedRscpSettingStore(inner, protector);
        await store.SaveSettingAsync(new(key, "test-only-secret", ControllerSettingSensitivity.Sensitive, Now));
        var raw = await inner.GetSettingAsync(key);
        Assert.NotNull(raw);
        Assert.DoesNotContain("test-only-secret", raw.Value!);
        var reconstructed = new ProtectedRscpSettingStore(inner, protector);
        var decoded = await reconstructed.GetSettingAsync(key);
        Assert.Equal("test-only-secret", decoded!.Value);
        Assert.Null(decoded.GetFrontendReadableValue());
        // A lost key ring must not break settings reads; the credential just counts as unconfigured.
        var wrongKeys = new ProtectedRscpSettingStore(inner, new EphemeralDataProtectionProvider());
        var unreadable = await wrongKeys.GetSettingAsync(key);
        Assert.NotNull(unreadable);
        Assert.False(unreadable.IsConfigured);
        Assert.Contains(await wrongKeys.GetAllSettingsAsync(), s => s.Key == key && !s.IsConfigured);
    }

    [Theory]
    [InlineData("normalEss", "External-ESS")]
    [InlineData("externalEss", "Anschluss")]
    public async Task MissingPreconditionsNeverClaimOwnershipOrCreateRecoveryObligation(string controlMode, string expectedReason)
    {
        // Claiming first used to persist a recovery obligation whose release could not succeed (e.g. normalEss),
        // which locked the very settings needed to repair it.
        var settings = new Settings();
        foreach (var (key, value) in new[] {
            (ControllerSettingDefaults.JointBatteryExecutionEnabledKey, "true"), (ControllerSettingDefaults.VictronDryRunKey, "false"),
            (ControllerSettingDefaults.E3dcRscpEnabledKey, "true"), (ControllerSettingDefaults.E3dcRscpHostKey, "192.0.2.10"),
            (ControllerSettingDefaults.E3dcRscpPortKey, "5033"), (ControllerSettingDefaults.E3dcRscpUserKey, "test-user"),
            (ControllerSettingDefaults.E3dcRscpPasswordKey, "test-password"), (ControllerSettingDefaults.E3dcRscpKeyKey, "test-key"),
            (ControllerSettingDefaults.VictronControlModeKey, controlMode), (ControllerSettingDefaults.JointBatteryPhase1LimitKey, "0") })
            await settings.SaveSettingAsync(new(key, value, ControllerSettingSensitivity.Normal, Now));
        var events = new Events();
        using var services = new ServiceCollection().AddSingleton<IControllerSettingStore>(settings)
            .AddSingleton<IUtcClock>(new Clock()).AddSingleton<IOperationalEventRepository>(events).BuildServiceProvider();
        var state = new JointBatteryExecutionState();
        var ownership = new VictronSetpointRefreshState();
        var worker = new JointBatteryExecutionBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), state,
            ownership, NullLogger<JointBatteryExecutionBackgroundService>.Instance, new Watchdog());
        await worker.TickAsync(CancellationToken.None);
        await worker.TickAsync(CancellationToken.None);
        Assert.Equal("Blocked", state.Current.Status);
        Assert.Contains(expectedReason, state.Current.Message);
        Assert.False(state.Current.OwnsControl);
        Assert.False(ownership.JointControlActive);
        Assert.Null(await settings.GetSettingAsync(ControllerSettingDefaults.JointBatteryRecoveryPendingKey));
        // A persistent blocker is logged once, not on every tick.
        Assert.Single(events.Saved);
    }

    [Fact]
    public async Task MissingPlanShowsThePreviewFailureReason()
    {
        var settings = new Settings();
        foreach (var (key, value) in new[] {
            (ControllerSettingDefaults.JointBatteryExecutionEnabledKey, "true"), (ControllerSettingDefaults.VictronDryRunKey, "false"),
            (ControllerSettingDefaults.E3dcRscpEnabledKey, "true"), (ControllerSettingDefaults.E3dcRscpHostKey, "192.0.2.10"),
            (ControllerSettingDefaults.E3dcRscpPortKey, "5033"), (ControllerSettingDefaults.E3dcRscpUserKey, "test-user"),
            (ControllerSettingDefaults.E3dcRscpPasswordKey, "test-password"), (ControllerSettingDefaults.E3dcRscpKeyKey, "test-key"),
            (ControllerSettingDefaults.VictronControlModeKey, "externalEss"), (ControllerSettingDefaults.JointBatteryPhase1LimitKey, "14720"),
            (ControllerSettingDefaults.JointBatteryPhase2LimitKey, "14720"), (ControllerSettingDefaults.JointBatteryPhase3LimitKey, "14720"),
            (ControllerSettingDefaults.JointBatteryVictronInputLimitKey, "14720") })
            await settings.SaveSettingAsync(new(key, value, ControllerSettingSensitivity.Normal, Now));
        var runtime = new JointBatteryRuntimeState();
        runtime.Set(new("Blocked", "Vorschau-Testgrund.", Now, null, null));
        using var services = new ServiceCollection().AddSingleton<IControllerSettingStore>(settings).AddSingleton<IUtcClock>(new Clock())
            .AddSingleton<IOperationalEventRepository>(new Events()).AddSingleton(runtime).BuildServiceProvider();
        var state = new JointBatteryExecutionState();
        var worker = new JointBatteryExecutionBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), state,
            new VictronSetpointRefreshState(), NullLogger<JointBatteryExecutionBackgroundService>.Instance, new Watchdog());
        await worker.TickAsync(CancellationToken.None);
        Assert.Equal("Blocked", state.Current.Status);
        Assert.Contains("Plan fehlt", state.Current.Message);
        Assert.Contains("Ursache: Vorschau-Testgrund.", state.Current.Message);
        Assert.False(state.Current.OwnsControl);
    }

    [Fact]
    public async Task ManualRecoveryConfirmationRequiresDisabledExecutionAndReleasesLegacyLock()
    {
        var settings = new Settings();
        await settings.SaveSettingAsync(new(ControllerSettingDefaults.JointBatteryRecoveryPendingKey, "true", ControllerSettingSensitivity.Normal, Now));
        await settings.SaveSettingAsync(new(ControllerSettingDefaults.JointBatteryExecutionEnabledKey, "true", ControllerSettingSensitivity.Normal, Now));
        var ownership = new VictronSetpointRefreshState { JointControlActive = true };
        var state = new JointBatteryExecutionState { Direction = 1, NeutralSamples = 2 };
        var events = new Events();
        var recovery = new JointBatteryRecoveryOverride(settings, ownership, state, new Clock(), events,
            NullLogger<JointBatteryRecoveryOverride>.Instance);

        Assert.Contains("deaktivieren", await recovery.ConfirmAsync(CancellationToken.None));
        Assert.True(ownership.JointControlActive);

        await settings.SaveSettingAsync(new(ControllerSettingDefaults.JointBatteryExecutionEnabledKey, "false", ControllerSettingSensitivity.Normal, Now));
        Assert.Null(await recovery.ConfirmAsync(CancellationToken.None));
        Assert.False(ownership.JointControlActive);
        Assert.Equal(0, state.Direction);
        Assert.False(await JointBatteryOwnership.BlocksLegacyAsync(settings, CancellationToken.None));
        Assert.Equal("JointBatteryRecoveryOverride", Assert.Single(events.Saved).Category);

        Assert.Contains("keine", await recovery.ConfirmAsync(CancellationToken.None));
    }

    private sealed class Events : IOperationalEventRepository
    {
        internal List<OperationalEvent> Saved { get; } = [];
        public Task SaveEventAsync(OperationalEvent operationalEvent, CancellationToken cancellationToken = default)
        { Saved.Add(operationalEvent); return Task.CompletedTask; }
        public Task<IReadOnlyList<OperationalEvent>> GetRecentEventsAsync(int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OperationalEvent>>(Saved);
    }

    [Fact]
    public async Task OtherSettingsKeepExistingFormat()
    {
        var inner = new Settings();
        var store = new ProtectedRscpSettingStore(inner, new EphemeralDataProtectionProvider());
        await store.SaveSettingAsync(new(ControllerSettingDefaults.VictronDryRunKey, "false", ControllerSettingSensitivity.Normal, Now));
        Assert.Equal("false", (await inner.GetSettingAsync(ControllerSettingDefaults.VictronDryRunKey))!.Value);
    }

    [Fact]
    public async Task DisabledFeatureDoesNotResolveAnyHardwareOrCommandServices()
    {
        var settings = new Settings();
        using var services = new ServiceCollection().AddSingleton<IControllerSettingStore>(settings)
            .AddSingleton<IUtcClock>(new Clock()).BuildServiceProvider();
        var state = new JointBatteryRuntimeState();
        var worker = new JointBatteryShadowBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), state,
            NullLogger<JointBatteryShadowBackgroundService>.Instance);
        await worker.PollAsync(CancellationToken.None);
        Assert.Equal("Disabled", state.Current.Status);
        Assert.Null(state.Current.Plan);
    }

    [Fact]
    public async Task MonitoringRequiresOnlyReadBoundaryAndNeverResolvesACommandPublisher()
    {
        var settings = new Settings();
        await settings.SaveSettingAsync(new(ControllerSettingDefaults.E3dcRscpEnabledKey, "true", ControllerSettingSensitivity.Normal, Now));
        using var services = new ServiceCollection().AddSingleton<IControllerSettingStore>(settings)
            .AddSingleton<IUtcClock>(new Clock()).AddSingleton<IE3dcTelemetrySource>(new ReadSource()).BuildServiceProvider();
        var state = new JointBatteryRuntimeState();
        var worker = new JointBatteryShadowBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), state,
            NullLogger<JointBatteryShadowBackgroundService>.Instance);
        await worker.PollAsync(CancellationToken.None);
        Assert.Equal("Monitoring", state.Current.Status);
        Assert.NotNull(state.Current.LocalTelemetry);
        Assert.Null(state.Current.Plan);
    }

    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => Now; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionDisabledOrDryRunNeverResolvesHardware(bool enabled)
    {
        var settings = new Settings();
        await settings.SaveSettingAsync(new(ControllerSettingDefaults.JointBatteryExecutionEnabledKey, enabled.ToString(), ControllerSettingSensitivity.Normal, Now));
        await settings.SaveSettingAsync(new(ControllerSettingDefaults.VictronDryRunKey, "true", ControllerSettingSensitivity.Normal, Now));
        using var services = new ServiceCollection().AddSingleton<IControllerSettingStore>(settings)
            .AddSingleton<IUtcClock>(new Clock()).BuildServiceProvider();
        var state = new JointBatteryExecutionState();
        var worker = new JointBatteryExecutionBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), state,
            new VictronSetpointRefreshState(), NullLogger<JointBatteryExecutionBackgroundService>.Instance, new Watchdog());
        await worker.TickAsync(CancellationToken.None);
        Assert.False(state.Current.OwnsControl);
        Assert.Equal(enabled ? "Shadow" : "Disabled", state.Current.Status);
    }

    [Theory]
    [InlineData(ControllerSettingDefaults.JointBatteryExecutionEnabledKey)]
    [InlineData(ControllerSettingDefaults.JointBatteryRecoveryPendingKey)]
    public async Task OwnershipFlagsPreventDeviceAddressEdits(string flag)
    {
        var settings = new Settings();
        await settings.SaveSettingAsync(new(flag, "true", ControllerSettingSensitivity.Normal, Now));
        var service = new TibberVictronController.Business.Services.ControllerSettingsService(settings, new Clock());
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateSettingAsync(ControllerSettingDefaults.E3dcRscpHostKey, "192.0.2.1"));
        Assert.True(await JointBatteryOwnership.BlocksLegacyAsync(settings, CancellationToken.None));
    }

    private sealed class Watchdog : IJointBatteryWatchdog
    {
        public bool IsSupervised => true;
        public Task PulseAsync(CancellationToken token) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartRecoversPersistedCommandsBeforeAllowingLegacyWriter(bool communicationFailure)
    {
        var settings = new Settings();
        await settings.SaveSettingAsync(new(ControllerSettingDefaults.JointBatteryRecoveryPendingKey, "true", ControllerSettingSensitivity.Normal, Now));
        var state = new JointBatteryExecutionState();
        var hardware = new RecoveryHardware(communicationFailure);
        using var services = new ServiceCollection().AddSingleton<IControllerSettingStore>(settings)
            .AddSingleton<IUtcClock>(new Clock()).AddSingleton(state).AddSingleton<IJointBatteryHardware>(hardware)
            .AddScoped<JointBatteryExecutionCoordinator>().BuildServiceProvider();
        var ownership = new VictronSetpointRefreshState();
        var worker = new JointBatteryExecutionBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), state,
            ownership, NullLogger<JointBatteryExecutionBackgroundService>.Instance, new Watchdog());
        if (communicationFailure)
            await Assert.ThrowsAsync<IOException>(() => worker.TickAsync(CancellationToken.None));
        else await worker.TickAsync(CancellationToken.None);
        Assert.Equal("Victron:0", hardware.Commands[0]);
        Assert.Equal(communicationFailure, ownership.JointControlActive);
        Assert.Equal(communicationFailure, await JointBatteryOwnership.BlocksLegacyAsync(settings, CancellationToken.None));
        Assert.Equal(!communicationFailure, hardware.Commands.Contains("E3dc:Automatic"));
    }

    private sealed class RecoveryHardware(bool fail) : IJointBatteryHardware
    {
        internal List<string> Commands { get; } = [];
        public Task SetVictronAsync(int watts, CancellationToken token)
        { Commands.Add("Victron:" + watts); return Task.CompletedTask; }
        public Task<E3dcPowerAcknowledgement> SetE3dcAsync(E3dcPowerCommand command, CancellationToken token)
        {
            Commands.Add("E3dc:" + command.Mode);
            if (fail) throw new IOException("Simulierter Verbindungsabbruch");
            return Task.FromResult(new E3dcPowerAcknowledgement(0, 0, 0, Now));
        }
        public Task<JointBatteryLiveSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(new JointBatteryLiveSnapshot(Now,
            [new("victron", new BatteryConfiguration(new BatteryConfigurationValues { TotalCapacityKwh = 10 }), new(50, Now), 0),
             new("e3dc", new BatteryConfiguration(new BatteryConfigurationValues { TotalCapacityKwh = 10 }), new(50, Now), 0)],
            [0, 0, 0], [0], 0, 0, new([10000, 10000, 10000], 5000, 1, 0)));
    }
    private sealed class ReadSource : IE3dcTelemetrySource
    {
        public Task<E3dcLocalTelemetry> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new E3dcLocalTelemetry(Now, 50, 0, 14, 1.5m, 3000, 3000, 3000, 3000, 1, true));
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
