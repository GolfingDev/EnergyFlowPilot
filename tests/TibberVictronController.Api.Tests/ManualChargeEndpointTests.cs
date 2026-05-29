using Microsoft.AspNetCore.Http.HttpResults;
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
            new FixedUtcClock(NowUtc),
            CancellationToken.None);

        var okResult = Assert.IsType<Ok<ManualChargeStatusDto>>(result);
        Assert.True(okResult.Value!.IsActive);
        Assert.Equal(2500, okResult.Value.PowerWatts);
        Assert.Equal(NowUtc.AddMinutes(30), okResult.Value.ExpiresAtUtc);
        Assert.Equal("2500", (await settingsStore.GetSettingAsync(ControllerSettingDefaults.ManualChargePowerWattsKey))!.Value);
    }

    [Fact]
    public async Task StopManualChargeAsyncDisablesStoredOverride()
    {
        var settingsStore = new FakeControllerSettingStore();

        var result = await ManualChargeEndpoints.StopManualChargeAsync(
            settingsStore,
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
            new FixedUtcClock(NowUtc),
            CancellationToken.None);

        Assert.IsType<BadRequest<ManualChargeErrorDto>>(result);
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
}
