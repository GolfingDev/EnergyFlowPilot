using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Api.Decision;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;

namespace TibberVictronController.Api.Tests;

public sealed class DecisionLogRetentionBackgroundServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly ServiceProvider services;

    public DecisionLogRetentionBackgroundServiceTests()
    {
        connection.Open();
        services = new ServiceCollection()
            .AddDbContext<ControllerDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IControllerSettingStore, EfControllerSettingStore>()
            .AddScoped<EfDecisionLogRepository>()
            .BuildServiceProvider();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControllerDbContext>();
        db.Database.EnsureCreated();
        // Decisions 200, 100, 60, 20 and 1 day old, each with a reason.
        foreach (var days in new[] { 200, 100, 60, 20, 1 })
        {
            var at = Now.AddDays(-days);
            db.DecisionLogEntries.Add(new DecisionLogEntryEntity { Id = Guid.NewGuid(), DecidedAtUtc = at, ValidFromUtc = at, ValidToUtc = at.AddMinutes(15),
                InputSummaryJson = "{}", Reasons = [new DecisionLogReasonEntity { RuleName = "Test", Message = $"{days} Tage" }] });
        }
        db.SaveChanges();
    }

    public void Dispose()
    {
        services.Dispose();
        connection.Dispose();
    }

    private async Task SetAsync(string key, string value)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IControllerSettingStore>().SaveSettingAsync(new(key, value, ControllerSettingSensitivity.Normal, Now));
    }

    private async Task BackfillsDoneAsync()
    {
        await SetAsync(ControllerSettingDefaults.EnergyHistoryBackfilledThroughKey, Now.AddDays(-1).ToString("O"));
        await SetAsync(ControllerSettingDefaults.PriceArchiveBackfilledThroughKey, Now.AddDays(-1).ToString("O"));
    }

    private async Task<int[]> RemainingAgesAsync()
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControllerDbContext>();
        var remaining = await db.DecisionLogEntries.AsNoTracking().Select(entry => entry.DecidedAtUtc).ToListAsync();
        Assert.Equal(remaining.Count, await db.DecisionLogReasons.CountAsync());
        return remaining.Select(at => (int)Math.Round((Now - at).TotalDays)).OrderDescending().ToArray();
    }

    private DecisionLogRetentionBackgroundService Service() =>
        new(services.GetRequiredService<IServiceScopeFactory>(), new Clock(), NullLogger<DecisionLogRetentionBackgroundService>.Instance)
        { BatchSize = 1, BatchPause = TimeSpan.Zero };

    [Fact]
    public async Task NothingIsDeletedUntilTheOperatorSwitchesTheCleanupOn()
    {
        await BackfillsDoneAsync();

        Assert.Equal(0, await Service().RunOnceAsync(CancellationToken.None));
        Assert.Equal(new[] { 200, 100, 60, 20, 1 }, await RemainingAgesAsync());
    }

    [Fact]
    public async Task CleanupWaitsForBothBackfills()
    {
        await SetAsync(ControllerSettingDefaults.DecisionLogCleanupEnabledKey, "true");
        await SetAsync(ControllerSettingDefaults.EnergyHistoryBackfilledThroughKey, Now.AddDays(-1).ToString("O"));

        Assert.Equal(0, await Service().RunOnceAsync(CancellationToken.None));
        Assert.Equal(5, (await RemainingAgesAsync()).Length);
    }

    [Fact]
    public async Task DeletesOnlyDecisionsOlderThanTheRetentionTogetherWithTheirReasonsInBatches()
    {
        await SetAsync(ControllerSettingDefaults.DecisionLogCleanupEnabledKey, "true");
        await BackfillsDoneAsync();

        Assert.Equal(2, await Service().RunOnceAsync(CancellationToken.None));
        Assert.Equal(new[] { 60, 20, 1 }, await RemainingAgesAsync());
    }

    [Fact]
    public async Task NeverKeepsLessThanThirtyDaysOrDeletesWhatABackfillHasNotProcessed()
    {
        await SetAsync(ControllerSettingDefaults.DecisionLogCleanupEnabledKey, "true");
        await SetAsync(ControllerSettingDefaults.DecisionLogRetentionDaysKey, "7");
        await SetAsync(ControllerSettingDefaults.EnergyHistoryBackfilledThroughKey, Now.AddDays(-1).ToString("O"));
        // The price archive import has only reached the day 150 days ago.
        await SetAsync(ControllerSettingDefaults.PriceArchiveBackfilledThroughKey, Now.AddDays(-150).ToString("O"));

        Assert.Equal(1, await Service().RunOnceAsync(CancellationToken.None));
        Assert.Equal(new[] { 100, 60, 20, 1 }, await RemainingAgesAsync());

        await SetAsync(ControllerSettingDefaults.PriceArchiveBackfilledThroughKey, Now.AddDays(-1).ToString("O"));
        Assert.Equal(2, await Service().RunOnceAsync(CancellationToken.None));
        Assert.Equal(new[] { 20, 1 }, await RemainingAgesAsync());
    }

    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => Now; }
}
