using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;

namespace TibberVictronController.Dal.Tests;

public sealed class PublishedPriceArchiveTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private ControllerDbContext db = null!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new ControllerDbContext(new DbContextOptionsBuilder<ControllerDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private static TibberPriceForecastSlot Price(int quarter, decimal price, bool estimated = false, string currency = "EUR") =>
        new(new ForecastTimeSlot(Start.AddMinutes(15 * quarter), Start.AddMinutes(15 * (quarter + 1))), price, currency) { IsEstimated = estimated };

    private void Log(int minute, decimal price, string json) => db.DecisionLogEntries.Add(new DecisionLogEntryEntity {
        Id = Guid.NewGuid(), DecidedAtUtc = Start.AddMinutes(minute), ValidFromUtc = Start, ValidToUtc = Start.AddHours(1),
        TibberPricePerKwh = price, TibberPriceCurrency = "EUR", InputSummaryJson = json });

    [Fact]
    public async Task StoresOnlyPublishedPricesAndTheLatestRecordingWins()
    {
        var archive = new EfPublishedPriceArchive(db);

        await archive.SaveAsync("tibber", [Price(0, .20m), Price(1, .30m, estimated: true), Price(2, .25m)], Start);
        await archive.SaveAsync("tibber", [Price(2, .26m)], Start.AddMinutes(15));
        await archive.SaveAsync("octopusGermany", [Price(0, .18m)], Start);

        var tibber = await archive.GetAsync(new(Start, Start.AddHours(1), "tibber", "EUR"));
        Assert.Equal([.20m, .26m], tibber.Select(p => p.TotalPricePerKwh));
        Assert.Equal([Start, Start.AddMinutes(30)], tibber.Select(p => p.TimeSlot.StartsAtUtc));
        Assert.Single(await archive.GetAsync(new(Start, Start.AddHours(1), "octopusGermany", "EUR")));
        Assert.Empty(await archive.GetAsync(new(Start, Start.AddHours(1), "tibber", "GBP")));
    }

    [Fact]
    public async Task ImportFromTheDecisionLogMatchesTheLoggedHistoryAndCanBeRepeated()
    {
        Log(1, .27m, "{\"ElectricityPriceProvider\":\"octopusGermany\"}");
        Log(2, .27m, "{\"ElectricityPriceProvider\":\"octopusGermany\"}");
        Log(16, .15m, "{\"ElectricityPriceProvider\":\"octopusGermany\"}");
        Log(31, .40m, "{\"ElectricityPriceProvider\":\"octopusGermany\",\"IsPriceEstimated\":true}");
        Log(3, .30m, "{}");                                   // untagged single-battery log: Tibber
        Log(5, .31m, "{\"ControlSource\":\"JointBattery\"}"); // untagged joint log: no supplier
        Log(6, .32m, "invalid");
        await db.SaveChangesAsync();
        var repository = new EfDecisionLogRepository(db);
        var octopus = new HistoricalPriceRequest(Start, Start.AddHours(1), "octopusGermany", "EUR");
        var loggedOnly = await repository.GetHistoricalPricesAsync(octopus);

        var archive = new EfPublishedPriceArchive(db);
        var imported = await archive.ImportFromDecisionLogAsync(Start, Start.AddDays(1), Start.AddDays(1));
        var again = await archive.ImportFromDecisionLogAsync(Start, Start.AddDays(1), Start.AddDays(1));

        Assert.Equal(3, imported); // two Octopus quarter hours and one Tibber quarter hour
        Assert.Equal(0, again);
        var fromArchive = await archive.GetAsync(octopus);
        Assert.Equal(loggedOnly.Select(p => (p.TimeSlot, Math.Round(p.TotalPricePerKwh, 6))), fromArchive.Select(p => (p.TimeSlot, Math.Round(p.TotalPricePerKwh, 6))));
        Assert.Single(await archive.GetAsync(new(Start, Start.AddHours(1), "tibber", "EUR")));
    }

    [Fact]
    public async Task ImportNeverOverridesARecordedSupplierPrice()
    {
        Log(1, .27m, "{\"ElectricityPriceProvider\":\"tibber\"}");
        await db.SaveChangesAsync();
        var archive = new EfPublishedPriceArchive(db);
        await archive.SaveAsync("tibber", [Price(0, .21m)], Start);

        await archive.ImportFromDecisionLogAsync(Start, Start.AddDays(1), Start.AddDays(1));

        Assert.Equal(.21m, Assert.Single(await archive.GetAsync(new(Start, Start.AddHours(1), "tibber", "EUR"))).TotalPricePerKwh);
    }

    [Fact]
    public async Task HistoricalPricesPreferTheArchiveAndFillOlderQuarterHoursFromTheDecisionLog()
    {
        // Quarter hour 0 only in the decision log (before the archive existed), quarter hour 1 in both, 2 only archived
        // (e.g. joint execution blocked, so no decision was logged).
        Log(1, .30m, "{\"ElectricityPriceProvider\":\"tibber\"}");
        Log(16, .99m, "{\"ElectricityPriceProvider\":\"tibber\"}");
        await db.SaveChangesAsync();
        await new EfPublishedPriceArchive(db).SaveAsync("tibber", [Price(1, .31m), Price(2, .32m)], Start);

        var prices = await new EfDecisionLogRepository(db).GetHistoricalPricesAsync(new(Start, Start.AddHours(1), "tibber", "EUR"));

        Assert.Equal([.30m, .31m, .32m], prices.Select(p => Math.Round(p.TotalPricePerKwh, 6)));
    }
}
