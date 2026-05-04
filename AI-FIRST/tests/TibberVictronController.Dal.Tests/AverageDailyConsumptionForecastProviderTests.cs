using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Consumption;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;

namespace TibberVictronController.Dal.Tests;

public sealed class AverageDailyConsumptionForecastProviderTests : IDisposable
{
    private static readonly DateTimeOffset SeededAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection sqliteConnection = new("Data Source=:memory:");

    public AverageDailyConsumptionForecastProviderTests()
    {
        sqliteConnection.Open();
    }

    [Fact]
    public async Task GetConsumptionForecastAsyncReturnsFifteenMinuteSlotsScaledToAverageDay()
    {
        await using var dbContext = CreateDbContext();
        var settingsStore = await CreateInitializedSettingStoreAsync(dbContext);
        var provider = new AverageDailyConsumptionForecastProvider(settingsStore, dbContext);

        var consumptionSlots = await provider.GetConsumptionForecastAsync(
            SeededAtUtc,
            SeededAtUtc.AddDays(1));

        var totalConsumption = consumptionSlots.Sum(slot => slot.ExpectedConsumptionKwh);
        var nightConsumption = GetLocalHourConsumption(consumptionSlots, localHour: 2);
        var eveningConsumption = GetLocalHourConsumption(consumptionSlots, localHour: 18);

        Assert.Equal(96, consumptionSlots.Count);
        Assert.All(consumptionSlots, slot => Assert.True(slot.TimeSlot.IsFifteenMinuteSlot));
        Assert.Equal(24.00m, totalConsumption);
        Assert.True(eveningConsumption > nightConsumption);
    }

    [Fact]
    public async Task GetConsumptionForecastAsyncUsesConfiguredAverageDailyConsumption()
    {
        await using var dbContext = CreateDbContext();
        var settingsStore = await CreateInitializedSettingStoreAsync(dbContext);
        await settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.ConsumptionForecastAverageDailyConsumptionKwhKey,
            "30",
            ControllerSettingSensitivity.Normal,
            SeededAtUtc.AddMinutes(5)));
        var provider = new AverageDailyConsumptionForecastProvider(settingsStore, dbContext);

        var consumptionSlots = await provider.GetConsumptionForecastAsync(
            SeededAtUtc,
            SeededAtUtc.AddDays(1));

        Assert.Equal(30.00m, consumptionSlots.Sum(slot => slot.ExpectedConsumptionKwh));
    }

    [Fact]
    public async Task GetConsumptionForecastAsyncRejectsNonUtcStart()
    {
        await using var dbContext = CreateDbContext();
        var settingsStore = await CreateInitializedSettingStoreAsync(dbContext);
        var provider = new AverageDailyConsumptionForecastProvider(settingsStore, dbContext);
        var localStart = new DateTimeOffset(2026, 5, 1, 2, 0, 0, TimeSpan.FromHours(2));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GetConsumptionForecastAsync(localStart, SeededAtUtc.AddHours(1)));

        Assert.Contains("Der Start des Verbrauchsforecast-Zeitraums muss in UTC angegeben sein.", exception.Message);
    }

    [Fact]
    public async Task GetConsumptionForecastAsyncBuildsWeekdayProfilesFromLiveSamples()
    {
        await using var dbContext = CreateDbContext();
        var settingsStore = await CreateInitializedSettingStoreAsync(dbContext);
        dbContext.LiveConsumptionSamples.AddRange(
            new LiveConsumptionSampleEntity
            {
                MeasuredAtUtc = new DateTimeOffset(2026, 5, 4, 6, 0, 0, TimeSpan.Zero),
                HouseConsumptionWatts = 800m
            },
            new LiveConsumptionSampleEntity
            {
                MeasuredAtUtc = new DateTimeOffset(2026, 5, 4, 6, 10, 0, TimeSpan.Zero),
                HouseConsumptionWatts = 1200m
            },
            new LiveConsumptionSampleEntity
            {
                MeasuredAtUtc = new DateTimeOffset(2026, 5, 5, 6, 5, 0, TimeSpan.Zero),
                HouseConsumptionWatts = 200m
            });
        await dbContext.SaveChangesAsync();
        var provider = new AverageDailyConsumptionForecastProvider(settingsStore, dbContext);

        var mondayForecast = await provider.GetConsumptionForecastAsync(
            new DateTimeOffset(2026, 5, 4, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 4, 6, 15, 0, TimeSpan.Zero));
        var tuesdayForecast = await provider.GetConsumptionForecastAsync(
            new DateTimeOffset(2026, 5, 5, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 5, 6, 15, 0, TimeSpan.Zero));

        Assert.Single(mondayForecast);
        Assert.Single(tuesdayForecast);
        Assert.Equal(0.25m, mondayForecast[0].ExpectedConsumptionKwh);
        Assert.Equal(0.05m, tuesdayForecast[0].ExpectedConsumptionKwh);
        Assert.NotEmpty(await dbContext.ConsumptionDayProfiles.ToListAsync());
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

    private static decimal GetLocalHourConsumption(
        IReadOnlyList<ConsumptionForecastSlot> consumptionSlots,
        int localHour)
    {
        var berlinTimeZone = ResolveBerlinTimeZone();

        return consumptionSlots
            .Where(slot => TimeZoneInfo.ConvertTime(slot.TimeSlot.StartsAtUtc, berlinTimeZone).Hour == localHour)
            .Sum(slot => slot.ExpectedConsumptionKwh);
    }

    private static TimeZoneInfo ResolveBerlinTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
    }
}
