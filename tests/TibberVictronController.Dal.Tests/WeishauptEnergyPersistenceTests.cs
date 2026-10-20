using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;

namespace TibberVictronController.Dal.Tests;

public sealed class WeishauptEnergyPersistenceTests
{
    [Fact]
    public async Task AdditiveUpgradePreservesLegacyRowsAndAllUnsignedCounters()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControllerDbContext>().UseSqlite(connection).Options;
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        await using (var db = new ControllerDbContext(options))
        {
            await new ControllerDbInitializer(db).InitializeAsync(now);
            // Only the isolated in-memory test table is replaced to simulate the old production schema.
            await db.Database.ExecuteSqlRawAsync("""
                DROP TABLE "WeishauptBiblockTelemetrySamples";
                CREATE TABLE "WeishauptBiblockTelemetrySamples" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "MeasuredAtUtc" INTEGER NOT NULL,
                    "OutdoorTemperatureCelsius" REAL NOT NULL,
                    "OperatingStatusCode" INTEGER NOT NULL,
                    "HotWaterTemperatureCelsius" REAL NOT NULL);
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "WeishauptBiblockTelemetrySamples"
                    ("MeasuredAtUtc", "OutdoorTemperatureCelsius", "OperatingStatusCode", "HotWaterTemperatureCelsius")
                VALUES ({now.ToUnixTimeMilliseconds()}, 10, 19, 45);
                """);
            await new ControllerDbInitializer(db).InitializeAsync(now);
            await new ControllerDbInitializer(db).InitializeAsync(now);
            var repository = new EfWeishauptBiblockTelemetryRepository(db);
            await repository.SaveSampleAsync(new WeishauptBiblockTelemetrySample
            {
                MeasuredAtUtc = now.AddMinutes(5), OutdoorTemperatureCelsius = 10,
                HotWaterTemperatureCelsius = 46, OperatingStatusCode = 20,
                TotalHeatTodayKwh = 0,
                TotalHeatYesterdayKwh = 1,
                TotalHeatMonthKwh = 2,
                TotalHeatYearKwh = 3,
                HeatingHeatTodayKwh = 4,
                HeatingHeatYesterdayKwh = 5,
                HeatingHeatMonthKwh = 6,
                HeatingHeatYearKwh = 7,
                HotWaterHeatTodayKwh = 8,
                HotWaterHeatYesterdayKwh = 9,
                HotWaterHeatMonthKwh = 10,
                HotWaterHeatYearKwh = 11,
                ElectricalTodayKwh = 12,
                ElectricalYesterdayKwh = 13,
                ElectricalMonthKwh = 14,
                ElectricalYearKwh = 65535
            });
        }
        await using var reloaded = new ControllerDbContext(options);
        var rows = await new EfWeishauptBiblockTelemetryRepository(reloaded).GetSamplesAsync(now, now.AddMinutes(10));
        Assert.Equal(2, rows.Count);
        Assert.Equal(45m, rows[0].HotWaterTemperatureCelsius);
        var properties = typeof(WeishauptBiblockTelemetrySample).GetProperties()
            .Where(p => p.PropertyType == typeof(ushort?)).ToArray();
        Assert.Equal(16, properties.Length);
        Assert.All(properties, p => Assert.Null(p.GetValue(rows[0])));
        Assert.Equal((ushort)0, rows[1].TotalHeatTodayKwh);
        Assert.Equal((ushort)1, rows[1].TotalHeatYesterdayKwh);
        Assert.Equal((ushort)2, rows[1].TotalHeatMonthKwh);
        Assert.Equal((ushort)3, rows[1].TotalHeatYearKwh);
        Assert.Equal((ushort)4, rows[1].HeatingHeatTodayKwh);
        Assert.Equal((ushort)5, rows[1].HeatingHeatYesterdayKwh);
        Assert.Equal((ushort)6, rows[1].HeatingHeatMonthKwh);
        Assert.Equal((ushort)7, rows[1].HeatingHeatYearKwh);
        Assert.Equal((ushort)8, rows[1].HotWaterHeatTodayKwh);
        Assert.Equal((ushort)9, rows[1].HotWaterHeatYesterdayKwh);
        Assert.Equal((ushort)10, rows[1].HotWaterHeatMonthKwh);
        Assert.Equal((ushort)11, rows[1].HotWaterHeatYearKwh);
        Assert.Equal((ushort)12, rows[1].ElectricalTodayKwh);
        Assert.Equal((ushort)13, rows[1].ElectricalYesterdayKwh);
        Assert.Equal((ushort)14, rows[1].ElectricalMonthKwh);
        Assert.Equal((ushort)65535, rows[1].ElectricalYearKwh);
    }
}
