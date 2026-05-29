using Microsoft.AspNetCore.Http.HttpResults;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Api.ManualCharge;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

public sealed class ManualChargeEndpointTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 5, 29, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartManualChargeAsyncStoresPowerAndExpiry()
    {
        var settingsStore = new FakeControllerSettingStore();

        var result = await ManualChargeEndpoints.StartManualChargeAsync(
            new ManualChargeRequestDto(30, 2.5m),
            settingsStore,
            new FakeCurrentBatteryDecisionService(CreateDecisionResult()),
            new FakeVictronSetpointPublisher(),
            new FixedUtcClock(NowUtc),
            CancellationToken.None);

        var okResult = Assert.IsType<Ok<ManualChargeStatusDto>>(result);
        Assert.True(okResult.Value!.IsActive);
        Assert.Equal(2500, okResult.Value.PowerWatts);
        Assert.Equal(NowUtc.AddMinutes(30), okResult.Value.ExpiresAtUtc);
        Assert.Equal("2500", (await settingsStore.GetSettingAsync(ControllerSettingDefaults.ManualChargePowerWattsKey))!.Value);
    }

    [Fact]
    public async Task StartManualChargeAsyncPublishesImmediateDecisionWhenDryRunIsDisabled()
    {
        var settingsStore = new FakeControllerSettingStore();
        await settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.VictronDryRunKey,
            "false",
            ControllerSettingSensitivity.Normal,
            NowUtc));
        var decisionResult = CreateDecisionResult();
        var publisher = new FakeVictronSetpointPublisher();

        await ManualChargeEndpoints.StartManualChargeAsync(
            new ManualChargeRequestDto(30, 2.5m),
            settingsStore,
            new FakeCurrentBatteryDecisionService(decisionResult),
            publisher,
            new FixedUtcClock(NowUtc),
            CancellationToken.None);

        Assert.Same(decisionResult, publisher.PublishedDecisionResult);
    }

    [Fact]
    public async Task StopManualChargeAsyncDisablesStoredOverride()
    {
        var settingsStore = new FakeControllerSettingStore();

        var result = await ManualChargeEndpoints.StopManualChargeAsync(
            settingsStore,
            new FakeCurrentBatteryDecisionService(CreateDecisionResult()),
            new FakeVictronSetpointPublisher(),
            new FixedUtcClock(NowUtc),
            CancellationToken.None);

        var okResult = Assert.IsType<Ok<ManualChargeStatusDto>>(result);
        Assert.False(okResult.Value!.IsActive);
        Assert.Equal("0", (await settingsStore.GetSettingAsync(ControllerSettingDefaults.ManualChargePowerWattsKey))!.Value);
    }

    [Fact]
    public async Task StartManualChargeAsyncRejectsInvalidDuration()
    {
        var result = await ManualChargeEndpoints.StartManualChargeAsync(
            new ManualChargeRequestDto(0, 2.5m),
            new FakeControllerSettingStore(),
            new FakeCurrentBatteryDecisionService(CreateDecisionResult()),
            new FakeVictronSetpointPublisher(),
            new FixedUtcClock(NowUtc),
            CancellationToken.None);

        Assert.IsType<BadRequest<ManualChargeErrorDto>>(result);
    }

    private static CurrentBatteryDecisionResult CreateDecisionResult()
    {
        return new CurrentBatteryDecisionResult(
            decidedAtUtc: NowUtc,
            validFromUtc: NowUtc,
            validToUtc: NowUtc.AddMinutes(30),
            decision: new CurrentBatteryDecision(
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
                2500),
            batteryState: new BatteryState(60m, NowUtc),
            siteTelemetry: new CurrentSiteTelemetry(0, 0, NowUtc),
            tibberPricePerKwh: null,
            tibberPriceCurrency: null,
            reasons: new[] { new BatteryDecisionReason("TEST", "Testentscheidung.") },
            inputSummaryJson: "{}");
    }

    private sealed class FakeControllerSettingStore : IControllerSettingStore
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

    private sealed class FakeCurrentBatteryDecisionService : ICurrentBatteryDecisionService
    {
        private readonly CurrentBatteryDecisionResult decisionResult;

        public FakeCurrentBatteryDecisionService(CurrentBatteryDecisionResult decisionResult)
        {
            this.decisionResult = decisionResult;
        }

        public Task<CurrentBatteryDecisionResult> CalculateCurrentDecisionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(decisionResult);
        }
    }

    private sealed class FakeVictronSetpointPublisher : IVictronSetpointPublisher
    {
        public CurrentBatteryDecisionResult? PublishedDecisionResult { get; private set; }

        public Task PublishAsync(CurrentBatteryDecisionResult decisionResult, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishedDecisionResult = decisionResult;
            return Task.CompletedTask;
        }
    }
}
