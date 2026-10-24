using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;

namespace TibberVictronController.Dal.Tests;

public sealed class HistoricalPriceTests
{
    [Fact]
    public async Task AggregationFiltersSourceCurrencyInvalidJsonAndDoesNotWeightPollingFrequency()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ControllerDbContext(new DbContextOptionsBuilder<ControllerDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var start = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        void Add(int minute, decimal price, string json, string currency = "EUR") => db.DecisionLogEntries.Add(new DecisionLogEntryEntity {
            Id = Guid.NewGuid(), DecidedAtUtc = start.AddMinutes(minute), ValidFromUtc = start, ValidToUtc = start.AddHours(1),
            TibberPricePerKwh = price, TibberPriceCurrency = currency, InputSummaryJson = json });
        Add(1, .27m, "{\"ElectricityPriceProvider\":\"octopus\"}");
        Add(2, .27m, "{\"ElectricityPriceProvider\":\"octopus\"}");
        Add(16, .15m, "{\"ElectricityPriceProvider\":\"octopus\"}");
        Add(3, 2m, "{\"ElectricityPriceProvider\":\"tibber\"}");
        Add(4, 2m, "{}");
        Add(5, 2m, "invalid");
        Add(7, 2m, "{\"ControlSource\":\"JointBattery\"}");
        Add(8, 2m, "{\"ElectricityPriceProvider\":\"octopus\",\"IsPriceEstimated\":true}");
        Add(6, 2m, "{\"ElectricityPriceProvider\":\"octopus\"}", "GBP");
        await db.SaveChangesAsync();
        var repository = new EfDecisionLogRepository(db);
        var actual = await repository.GetHistoricalPricesAsync(new(start, start.AddHours(1), "octopus", "EUR"));
        Assert.Equal(2, actual.Count);
        Assert.Equal(start, actual[0].TimeSlot.StartsAtUtc);
        Assert.InRange(actual[0].TotalPricePerKwh, .269999m, .270001m);
        Assert.InRange(actual.Average(p => p.TotalPricePerKwh), .209999m, .210001m);
        Assert.Single(await repository.GetHistoricalPricesAsync(new(start, start.AddHours(1), "tibber", "EUR")));
    }
}
