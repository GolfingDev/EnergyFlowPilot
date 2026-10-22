using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Tests;

public sealed class SupplementaryGridChargeHardwareTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T12:47:00Z");

    [Fact]
    public async Task ConfiguredSecondaryWithoutPowerReadingsBlocksAtTheRealHardwareAdapter()
    {
        var store = new Settings();
        await store.Set(ControllerSettingDefaults.HagerEnergyClientIdKey, "test-client");
        await store.Set(ControllerSettingDefaults.HagerEnergyClientSecretKey, "test-only-secret");
        await store.Set(ControllerSettingDefaults.HagerEnergyInstallationIdKey, "test-installation");
        var safety = new SupplementaryGridChargeSafety(new TibberVictronController.Dal.HagerEnergy.HagerEnergyTelemetrySnapshotStore(),
            new TibberVictronController.Dal.HagerEnergy.DatabaseHagerEnergySettingsProvider(store), new SupplementaryGridChargeGuard(),
            new Clock(), NullLogger<SupplementaryGridChargeSafety>.Instance);
        Assert.Equal(CurrentBatteryDecisionRuleIds.SupplementaryTelemetryMissing, (await safety.CheckAsync(default))?.RuleName);
    }

    [Theory]
    [InlineData("externalEss")]
    [InlineData("normalEss")]
    public async Task PublisherStopsImmediatelyInsteadOfRampingAndPreservesAcOutSupply(string mode)
    {
        var store = new Settings();
        await store.Set(ControllerSettingDefaults.VictronControlModeKey, mode);
        var settings = new DatabaseVictronMqttSettingsProvider(store);
        var topics = VictronMqttTopicFactory.Create(await settings.GetSettingsAsync());
        var client = new Client();
        foreach (var topic in topics.AcOutPowerTopics) client.Values[topic] = 600;
        var state = new VictronSetpointRefreshState();
        state.Update(new(Now.AddMinutes(15), [], [], 4000, 4000) { RequiresGridChargeProtection = true });
        var safety = new Safety { Blocked = true };
        var publisher = new MqttVictronSetpointPublisher(settings, client, state, NullLogger<MqttVictronSetpointPublisher>.Instance, safety);
        var decision = new CurrentBatteryDecisionResult(Now, Now, Now.AddMinutes(15),
            new CurrentBatteryDecision(new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid), 3846),
            new BatteryState(79, Now), new CurrentSiteTelemetry(27, 3090, Now, 3030), .2873m, "EUR", [new("TEST", "Test")], "{}");
        await publisher.PublishAsync(decision);
        Assert.True(state.TryGetLatest(out var stopped));
        Assert.Equal(0, stopped.SignedBatteryTargetWatts);
        Assert.Equal(0, stopped.DesiredSignedBatteryTargetWatts);
        Assert.Equal(1, client.Published[topics.DisableChargeTopic]);
        Assert.Equal(1, client.Published[topics.DisableFeedInTopic]);
        if (mode == "externalEss") Assert.All(topics.ExternalEssAcPowerSetpointTopics, t => Assert.Equal(600, client.Published[t]));
        else Assert.Equal(27 - 3030, client.Published[topics.ChargeDischargeSetpointTopic]);
    }

    [Fact]
    public async Task RefreshLatchesZeroUntilANewDecisionAndDoesNotResumeAnOldTarget()
    {
        var store = new Settings(); await store.Set(ControllerSettingDefaults.VictronControlModeKey, "externalEss");
        var settings = new DatabaseVictronMqttSettingsProvider(store);
        var topics = VictronMqttTopicFactory.Create(await settings.GetSettingsAsync());
        var client = new Client();
        foreach (var topic in topics.AcOutPowerTopics) client.Values[topic] = 600;
        var state = new VictronSetpointRefreshState();
        state.Update(new(Now.AddMinutes(15), [], [], 4000, 4000) { RequiresGridChargeProtection = true });
        var safety = new Safety { Blocked = true };
        var trigger = new DecisionCalculationTrigger();
        var services = new ServiceCollection().AddSingleton(trigger).AddSingleton<IUtcClock>(new Clock()).AddSingleton<IControllerSettingStore>(store)
            .AddScoped<DatabaseVictronMqttSettingsProvider>().AddSingleton<ISupplementaryGridChargeSafety>(safety).BuildServiceProvider();
        await using (services)
        {
            var refresh = new VictronSetpointRefreshBackgroundService(services, state, client, NullLogger<VictronSetpointRefreshBackgroundService>.Instance);
            await refresh.RefreshAsync(default);
            Assert.True(await trigger.WaitAsync(TimeSpan.Zero, default));
            await refresh.RefreshAsync(default);
            Assert.False(await trigger.WaitAsync(TimeSpan.Zero, default));
            safety.Blocked = false; // A clean reading must not revive the old 4 kW command.
            await refresh.RefreshAsync(default);
            Assert.True(state.TryGetLatest(out var stopped));
            Assert.Equal(0, stopped.SignedBatteryTargetWatts);
            Assert.Equal(0, stopped.DesiredSignedBatteryTargetWatts);
            Assert.All(topics.ExternalEssAcPowerSetpointTopics, t => Assert.Equal(600, client.Published[t]));
            Assert.Equal(1, client.Published[topics.DisableChargeTopic]);
        }
    }

    [Fact]
    public async Task PublicationGateSerializesDecisionAndRefreshWriteBatches()
    {
        var state = new VictronSetpointRefreshState();
        var first = await state.AcquirePublicationAsync(default);
        var second = state.AcquirePublicationAsync(default);
        Assert.False(second.IsCompleted);
        first.Dispose();
        using var next = await second;
    }

    private sealed class Safety : ISupplementaryGridChargeSafety
    {
        public bool Blocked { get; set; }
        public Task<BatteryDecisionReason?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<BatteryDecisionReason?>(Blocked
            ? new(CurrentBatteryDecisionRuleIds.SupplementaryGridChargeBlocked, "E3/DC entlaedt mit 774 W.") : null);
    }
    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => Now; }
    private sealed class Client : IVictronMqttControlClient
    {
        public Dictionary<string, decimal> Values { get; } = [];
        public Dictionary<string, int> Published { get; } = [];
        public bool TryGetLatestValue(string topic, out decimal value) => Values.TryGetValue(topic, out value);
        public Task PublishValueAsync(string topic, int value, CancellationToken cancellationToken = default) { Published[topic] = value; return Task.CompletedTask; }
    }
    private sealed class Settings : IControllerSettingStore
    {
        private readonly Dictionary<string, ControllerSetting> items = ControllerSettingDefaults.CreateDefaultSettings(Now).ToDictionary(s => s.Key);
        public Settings() { items[ControllerSettingDefaults.VictronDryRunKey] = new(ControllerSettingDefaults.VictronDryRunKey, "false", ControllerSettingSensitivity.Normal, Now); }
        public Task Set(string key, string value) => SaveSettingAsync(new(key, value, ControllerSettingSensitivity.Normal, Now));
        public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ControllerSetting>>(items.Values.ToArray());
        public Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(items.GetValueOrDefault(key));
        public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default) { items[setting.Key] = setting; return Task.CompletedTask; }
    }
}
