using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Services;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class ControllerSettingsServiceTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UpdateSettingAsyncPersistsKnownSettingWithDefaultSensitivity()
    {
        var settingStore = new InMemoryControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(NowUtc));
        var service = new ControllerSettingsService(settingStore, new FixedUtcClock(NowUtc.AddMinutes(5)));

        var setting = await service.UpdateSettingAsync(
            ControllerSettingDefaults.BatteryTotalCapacityKwhKey,
            "12",
            CancellationToken.None);

        Assert.Equal("12", setting.Value);
        Assert.Equal(ControllerSettingSensitivity.Normal, setting.Sensitivity);
        Assert.Equal(NowUtc.AddMinutes(5), setting.UpdatedAtUtc);
    }

    [Fact]
    public async Task UpdateSettingAsyncDoesNotExposeSensitiveSettingValueForFrontendReads()
    {
        var settingStore = new InMemoryControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(NowUtc));
        var service = new ControllerSettingsService(settingStore, new FixedUtcClock(NowUtc));

        var setting = await service.UpdateSettingAsync(
            ControllerSettingDefaults.TibberAccessTokenKey,
            "secret-token",
            CancellationToken.None);

        Assert.True(setting.IsConfigured);
        Assert.Equal(ControllerSettingSensitivity.Sensitive, setting.Sensitivity);
        Assert.Null(setting.GetFrontendReadableValue());
    }

    [Fact]
    public async Task UpdateSettingAsyncRejectsUnknownSettingKey()
    {
        var settingStore = new InMemoryControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(NowUtc));
        var service = new ControllerSettingsService(settingStore, new FixedUtcClock(NowUtc));

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.UpdateSettingAsync("unknown.setting", "value", CancellationToken.None));

        Assert.Contains("Die Einstellung 'unknown.setting' ist nicht bekannt.", exception.Message);
    }

    [Fact]
    public async Task UpdateSettingAsyncRejectsEmptyNormalSettingValue()
    {
        var settingStore = new InMemoryControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(NowUtc));
        var service = new ControllerSettingsService(settingStore, new FixedUtcClock(NowUtc));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateSettingAsync(
                ControllerSettingDefaults.BatteryTotalCapacityKwhKey,
                " ",
                CancellationToken.None));

        Assert.Contains("Die Einstellung 'battery.totalCapacityKwh' braucht einen Wert.", exception.Message);
    }

    [Fact]
    public async Task GetStatusAsyncSummarizesPersistedSettingsWithoutSecrets()
    {
        var settingStore = new InMemoryControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(NowUtc));
        var service = new ControllerSettingsService(settingStore, new FixedUtcClock(NowUtc));

        await service.UpdateSettingAsync(ControllerSettingDefaults.TibberAccessTokenKey, "secret-token", CancellationToken.None);
        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal("Healthy", status.Status);
        Assert.Equal(ControllerSettingDefaults.GetDefinitions().Count, status.KnownSettingsCount);
        Assert.Equal(1, status.ConfiguredSensitiveSettingsCount);
        Assert.Equal(NowUtc, status.GeneratedAtUtc);
    }

    private sealed class FixedUtcClock : IUtcClock
    {
        public FixedUtcClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class InMemoryControllerSettingStore : IControllerSettingStore
    {
        private readonly Dictionary<string, ControllerSetting> settings;

        public InMemoryControllerSettingStore(IEnumerable<ControllerSetting> settings)
        {
            this.settings = settings.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<IReadOnlyList<ControllerSetting>>(settings.Values.OrderBy(setting => setting.Key).ToArray());
        }

        public Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            settings.TryGetValue(key, out var setting);

            return Task.FromResult(setting);
        }

        public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            settings[setting.Key] = setting;

            return Task.CompletedTask;
        }
    }
}
