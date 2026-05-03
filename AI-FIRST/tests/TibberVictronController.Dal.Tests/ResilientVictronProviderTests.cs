using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Battery;
using TibberVictronController.Dal.Mqtt;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Dal.Tests;

public sealed class ResilientVictronProviderTests : IDisposable
{
    private static readonly DateTimeOffset SeededAtUtc = new(2026, 5, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MeasuredAtUtc = new(2026, 5, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection sqliteConnection = new("Data Source=:memory:");

    public ResilientVictronProviderTests()
    {
        sqliteConnection.Open();
    }

    [Fact]
    public async Task ResilientBatteryStateProviderFallsBackToConfiguredState()
    {
        await using var dbContext = CreateDbContext();
        var settingsStore = await CreateInitializedSettingStoreAsync(dbContext);
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.BatteryTemporaryStateOfChargePercentKey, "41.5"));
        var runtimeStatus = new VictronMqttRuntimeStatus();
        var provider = new ResilientBatteryStateProvider(
            new MqttBatteryStateProvider(new MqttTelemetrySnapshotStore()),
            new ConfiguredBatteryStateProvider(settingsStore, new FixedUtcClock(MeasuredAtUtc)),
            runtimeStatus);

        var batteryState = await provider.GetCurrentBatteryStateAsync();

        Assert.Equal(41.5m, batteryState.StateOfChargePercent);
        Assert.Equal("Failed", runtimeStatus.ConnectionState);
        Assert.NotNull(runtimeStatus.LastErrorMessage);
    }

    [Fact]
    public async Task ResilientCurrentSiteTelemetryProviderFallsBackToConfiguredTelemetry()
    {
        await using var dbContext = CreateDbContext();
        var settingsStore = await CreateInitializedSettingStoreAsync(dbContext);
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.TelemetryTemporaryGridImportWattsKey, "800"));
        await settingsStore.SaveSettingAsync(CreateNormalSetting(ControllerSettingDefaults.TelemetryTemporaryPvProductionWattsKey, "250"));
        var runtimeStatus = new VictronMqttRuntimeStatus();
        var provider = new ResilientCurrentSiteTelemetryProvider(
            new MqttCurrentSiteTelemetryProvider(new MqttTelemetrySnapshotStore()),
            new ConfiguredCurrentSiteTelemetryProvider(settingsStore, new FixedUtcClock(MeasuredAtUtc)),
            runtimeStatus);

        var telemetry = await provider.GetCurrentSiteTelemetryAsync();

        Assert.Equal(800, telemetry.CurrentGridImportWatts);
        Assert.Equal(250, telemetry.CurrentPvProductionWatts);
        Assert.Equal("Failed", runtimeStatus.ConnectionState);
        Assert.NotNull(runtimeStatus.LastErrorMessage);
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
