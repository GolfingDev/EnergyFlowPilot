using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;

namespace TibberVictronController.Dal.Tests;

public sealed class SqlitePersistenceTests : IDisposable
{
    private static readonly DateTimeOffset SeededAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection sqliteConnection = new("Data Source=:memory:");

    public SqlitePersistenceTests()
    {
        sqliteConnection.Open();
    }

    [Fact]
    public async Task InitializeAsyncCreatesDatabaseAndSeedsDefaultSettings()
    {
        await using var dbContext = CreateDbContext();
        var initializer = new ControllerDbInitializer(dbContext);

        await initializer.InitializeAsync(SeededAtUtc);

        var settings = await dbContext.ControllerSettings.ToListAsync();
        Assert.Equal(ControllerSettingDefaults.GetDefinitions().Count, settings.Count);
        Assert.Contains(settings, setting => setting.Key == ControllerSettingDefaults.TibberAccessTokenKey && setting.Value == null);
        Assert.Contains(settings, setting => setting.Key == ControllerSettingDefaults.PvForecastProviderKey && setting.Value == "forecastSolarPublic");
    }

    [Fact]
    public async Task InitializeAsyncAddsMissingDefaultsWithoutOverwritingExistingUserValues()
    {
        await using var dbContext = CreateDbContext();
        var initializer = new ControllerDbInitializer(dbContext);
        await initializer.InitializeAsync(SeededAtUtc);
        var settingsStore = new EfControllerSettingStore(dbContext);
        var userUpdatedAtUtc = SeededAtUtc.AddHours(1);

        await settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.BatteryTotalCapacityKwhKey,
            "15",
            ControllerSettingSensitivity.Normal,
            userUpdatedAtUtc));
        var removedSetting = await dbContext.ControllerSettings.SingleAsync(setting =>
            setting.Key == ControllerSettingDefaults.PvForecastTimeZoneKey);
        dbContext.ControllerSettings.Remove(removedSetting);
        await dbContext.SaveChangesAsync();

        await initializer.InitializeAsync(SeededAtUtc.AddHours(2));

        var preservedSetting = await settingsStore.GetSettingAsync(ControllerSettingDefaults.BatteryTotalCapacityKwhKey);
        var repairedSetting = await settingsStore.GetSettingAsync(ControllerSettingDefaults.PvForecastTimeZoneKey);
        Assert.Equal("15", preservedSetting?.Value);
        Assert.Equal(userUpdatedAtUtc, preservedSetting?.UpdatedAtUtc);
        Assert.Equal("Europe/Berlin", repairedSetting?.Value);
    }

    [Fact]
    public async Task ControllerSettingStoreSavesAndLoadsSensitiveSettings()
    {
        await using var dbContext = CreateDbContext();
        await new ControllerDbInitializer(dbContext).InitializeAsync(SeededAtUtc);
        var settingsStore = new EfControllerSettingStore(dbContext);

        await settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.TibberAccessTokenKey,
            "secret-token",
            ControllerSettingSensitivity.Sensitive,
            SeededAtUtc.AddMinutes(5)));

        var setting = await settingsStore.GetSettingAsync(ControllerSettingDefaults.TibberAccessTokenKey);
        Assert.True(setting?.IsConfigured);
        Assert.Equal(ControllerSettingSensitivity.Sensitive, setting?.Sensitivity);
        Assert.Null(setting?.GetFrontendReadableValue());
    }

    [Fact]
    public async Task DecisionLogRepositoryPersistsDecisionWithStructuredReasonsAndDeletesOldEntries()
    {
        await using var dbContext = CreateDbContext();
        await new ControllerDbInitializer(dbContext).InitializeAsync(SeededAtUtc);
        var repository = new EfDecisionLogRepository(dbContext);
        var oldEntry = CreateDecisionLogEntry(SeededAtUtc.AddDays(-100));
        var recentEntry = CreateDecisionLogEntry(SeededAtUtc.AddHours(1));

        await repository.SaveDecisionAsync(oldEntry);
        await repository.SaveDecisionAsync(recentEntry);
        var deletedCount = await repository.DeleteDecisionsOlderThanAsync(SeededAtUtc.AddDays(-90));

        var recentEntries = await repository.GetRecentDecisionsAsync(10);
        Assert.Equal(1, deletedCount);
        Assert.Single(recentEntries);
        Assert.Equal(recentEntry.Id, recentEntries[0].Id);
        Assert.Equal("TestRule", recentEntries[0].Reasons[0].RuleName);
    }

    [Fact]
    public async Task OperationalEventRepositoryPersistsEvents()
    {
        await using var dbContext = CreateDbContext();
        await new ControllerDbInitializer(dbContext).InitializeAsync(SeededAtUtc);
        var repository = new EfOperationalEventRepository(dbContext);
        var operationalEvent = new OperationalEvent(
            Guid.NewGuid(),
            SeededAtUtc,
            "TibberApi",
            "Error",
            "Tibber API nicht erreichbar.",
            "HTTP 500");

        await repository.SaveEventAsync(operationalEvent);

        var events = await repository.GetRecentEventsAsync(10);
        Assert.Single(events);
        Assert.Equal("TibberApi", events[0].Category);
        Assert.Equal("HTTP 500", events[0].Details);
    }

    public void Dispose()
    {
        sqliteConnection.Dispose();
    }

    private ControllerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ControllerDbContext>()
            .UseSqlite(sqliteConnection)
            .Options;

        return new ControllerDbContext(options);
    }

    private static DecisionLogEntry CreateDecisionLogEntry(DateTimeOffset decidedAtUtc)
    {
        var instruction = new BatteryDecisionInstruction(BatteryDecisionState.Discharge, chargeSource: null);
        var currentDecision = new CurrentBatteryDecision(instruction, targetPowerWatts: 1200);

        return new DecisionLogEntry(
            Guid.NewGuid(),
            decidedAtUtc,
            decidedAtUtc,
            decidedAtUtc.AddMinutes(15),
            currentDecision,
            stateOfChargePercent: 55m,
            tibberPricePerKwh: 0.30m,
            tibberPriceCurrency: "EUR",
            gridImportWatts: 1200,
            gridExportWatts: null,
            inputSummaryJson: """{"source":"test"}""",
            new[] { new BatteryDecisionReason("TestRule", "Testbegruendung") });
    }
}
