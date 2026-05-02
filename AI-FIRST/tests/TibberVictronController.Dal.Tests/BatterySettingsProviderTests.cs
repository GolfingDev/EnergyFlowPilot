using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Battery;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;

namespace TibberVictronController.Dal.Tests;

public sealed class BatterySettingsProviderTests : IDisposable
{
    private static readonly DateTimeOffset SeededAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MeasuredAtUtc = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection sqliteConnection = new("Data Source=:memory:");

    public BatterySettingsProviderTests()
    {
        sqliteConnection.Open();
    }

    [Fact]
    public async Task DatabaseBatteryConfigurationProviderLoadsBatteryConfigurationFromSettings()
    {
        await using var dbContext = CreateDbContext();
        var settingsStore = await CreateInitializedSettingStoreAsync(dbContext);
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.BatteryTotalCapacityKwhKey, "12"));
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.BatteryMinimumStateOfChargePercentKey, "15"));
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.BatteryMaximumChargePowerWattsKey, "3000"));
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.BatteryMaximumDischargePowerWattsKey, "2500"));
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.BatteryRoundTripEfficiencyPercentKey, "92.5"));
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.BatteryTargetEndStateOfChargePercentKey, "25"));
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.BatteryPlanningMinimumStateOfChargePercentKey, "18"));
        var provider = new DatabaseBatteryConfigurationProvider(settingsStore);

        var configuration = await provider.GetBatteryConfigurationAsync();

        Assert.Equal(12m, configuration.TotalCapacityKwh);
        Assert.Equal(15m, configuration.MinimumStateOfChargePercent);
        Assert.Equal(3000, configuration.MaximumChargePowerWatts);
        Assert.Equal(2500, configuration.MaximumDischargePowerWatts);
        Assert.Equal(92.5m, configuration.RoundTripEfficiencyPercent);
        Assert.Equal(25m, configuration.TargetEndStateOfChargePercent);
        Assert.Equal(18m, configuration.PlanningMinimumStateOfChargePercent);
    }

    [Fact]
    public async Task DatabaseBatteryConfigurationProviderRejectsInvalidCapacitySetting()
    {
        await using var dbContext = CreateDbContext();
        var settingsStore = await CreateInitializedSettingStoreAsync(dbContext);
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.BatteryTotalCapacityKwhKey, "ungueltig"));
        var provider = new DatabaseBatteryConfigurationProvider(settingsStore);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetBatteryConfigurationAsync());

        Assert.Contains("Die Batteriekapazitaet muss als Dezimalzahl konfiguriert sein.", exception.Message);
    }

    [Fact]
    public async Task ConfiguredBatteryStateProviderLoadsTemporaryStateOfChargeFromSettings()
    {
        await using var dbContext = CreateDbContext();
        var settingsStore = await CreateInitializedSettingStoreAsync(dbContext);
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.BatteryTemporaryStateOfChargePercentKey, "37.5"));
        var provider = new ConfiguredBatteryStateProvider(settingsStore, new FixedUtcClock(MeasuredAtUtc));

        var batteryState = await provider.GetCurrentBatteryStateAsync();

        Assert.Equal(37.5m, batteryState.StateOfChargePercent);
        Assert.Equal(MeasuredAtUtc, batteryState.MeasuredAtUtc);
    }

    [Fact]
    public async Task ConfiguredBatteryStateProviderRejectsInvalidStateOfChargeSetting()
    {
        await using var dbContext = CreateDbContext();
        var settingsStore = await CreateInitializedSettingStoreAsync(dbContext);
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.BatteryTemporaryStateOfChargePercentKey, "150"));
        var provider = new ConfiguredBatteryStateProvider(settingsStore, new FixedUtcClock(MeasuredAtUtc));

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => provider.GetCurrentBatteryStateAsync());

        Assert.Contains("Der Akkuladestand muss zwischen 0 und 100 Prozent liegen.", exception.Message);
    }

    public void Dispose()
    {
        sqliteConnection.Dispose();
    }

    private async Task<EfControllerSettingStore> CreateInitializedSettingStoreAsync(ControllerDbContext dbContext)
    {
        await new ControllerDbInitializer(dbContext).InitializeAsync(SeededAtUtc);

        return new EfControllerSettingStore(dbContext);
    }

    private ControllerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ControllerDbContext>()
            .UseSqlite(sqliteConnection)
            .Options;

        return new ControllerDbContext(options);
    }

    private static ControllerSetting CreateNormalSetting(string key, string value)
    {
        return new ControllerSetting(key, value, ControllerSettingSensitivity.Normal, SeededAtUtc.AddMinutes(5));
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
