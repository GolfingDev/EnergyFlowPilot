using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Api.Prices;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;

namespace TibberVictronController.Api.Tests;

public sealed class PublishedPriceArchiveBackgroundServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly ServiceProvider services;

    public PublishedPriceArchiveBackgroundServiceTests()
    {
        connection.Open();
        services = new ServiceCollection()
            .AddDbContext<ControllerDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IControllerSettingStore, EfControllerSettingStore>()
            .AddScoped<EfPublishedPriceArchive>()
            .AddScoped<IPublishedPriceArchive>(provider => provider.GetRequiredService<EfPublishedPriceArchive>())
            .AddScoped<IElectricityPriceForecastProvider, Prices>()
            .BuildServiceProvider();
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ControllerDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        services.Dispose();
        connection.Dispose();
    }

    private PublishedPriceArchiveBackgroundService Service() =>
        new(services.GetRequiredService<IServiceScopeFactory>(), new Clock(), NullLogger<PublishedPriceArchiveBackgroundService>.Instance)
        { BackfillPause = TimeSpan.Zero };

    private async Task<IReadOnlyList<TibberPriceForecastSlot>> ArchivedAsync(DateTimeOffset from, DateTimeOffset to)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPublishedPriceArchive>().GetAsync(new(from, to, "tibber", "EUR"));
    }

    [Fact]
    public async Task RecordingStoresThePublishedPricesOfTheSelectedSupplier()
    {
        await Service().RecordAsync(CancellationToken.None);

        var archived = await ArchivedAsync(Now.AddHours(-12), Now.AddHours(48));
        Assert.Equal([Now, Now.AddMinutes(15)], archived.Select(p => p.TimeSlot.StartsAtUtc));
    }

    [Fact]
    public async Task BackfillImportsCompleteDaysOnceAndRemembersItsProgress()
    {
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControllerDbContext>();
            foreach (var at in new[] { Now.AddDays(-2), Now.AddDays(-1), Now })
                db.DecisionLogEntries.Add(new DecisionLogEntryEntity { Id = Guid.NewGuid(), DecidedAtUtc = at, ValidFromUtc = at, ValidToUtc = at.AddMinutes(15),
                    TibberPricePerKwh = .3m, TibberPriceCurrency = "EUR", InputSummaryJson = "{\"ElectricityPriceProvider\":\"tibber\"}" });
            await db.SaveChangesAsync();
        }

        await Service().BackfillAsync(CancellationToken.None);

        // The two complete UTC days before today; today is covered by the recording and later runs.
        Assert.Equal(2, (await ArchivedAsync(Now.AddDays(-3), Now.AddDays(1))).Count);
        await using (var scope = services.CreateAsyncScope())
        {
            var progress = await scope.ServiceProvider.GetRequiredService<IControllerSettingStore>()
                .GetSettingAsync(ControllerSettingDefaults.PriceArchiveBackfilledThroughKey);
            Assert.Equal(new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero), DateTimeOffset.Parse(progress!.Value!));
        }
    }

    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => Now; }

    private sealed class Prices : IElectricityPriceForecastProvider
    {
        public string Source => "tibber";

        public Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TibberPriceForecastSlot>>([
                new(new ForecastTimeSlot(Now, Now.AddMinutes(15)), .30m, "EUR"),
                new(new ForecastTimeSlot(Now.AddMinutes(15), Now.AddMinutes(30)), .31m, "EUR"),
                new(new ForecastTimeSlot(Now.AddMinutes(30), Now.AddMinutes(45)), .29m, "EUR") { IsEstimated = true }]);
    }
}
