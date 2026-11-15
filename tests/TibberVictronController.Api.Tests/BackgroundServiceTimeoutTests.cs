using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Api.Decision;
using TibberVictronController.Api.Energy;
using TibberVictronController.Api.EvCharging;
using TibberVictronController.Api.Prices;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

/// <summary>
/// An HTTP timeout is a <see cref="TaskCanceledException"/> although nobody stops the service. If it left
/// ExecuteAsync, the host would stop the whole process (default <c>StopHost</c>), including the battery control.
/// Each test lets a dependency time out and expects the loop to try again after its pause and to end on shutdown.
/// </summary>
public sealed class BackgroundServiceTimeoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ShortPause = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task PriceArchiveKeepsRunningWhenTheSupplierTimesOut()
    {
        var attempts = new Attempts();
        using var services = new ServiceCollection()
            .AddSingleton(attempts)
            .AddScoped<IControllerSettingStore, EmptySettings>()
            .AddScoped<IPublishedPriceArchive, UnusedArchive>()
            .AddScoped<IElectricityPriceForecastProvider, TimingOutPrices>()
            .BuildServiceProvider();
        var service = new PublishedPriceArchiveBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), new Clock(),
            NullLogger<PublishedPriceArchiveBackgroundService>.Instance)
        { StartupDelay = TimeSpan.Zero, Interval = ShortPause, BackfillPause = TimeSpan.Zero };

        await AssertSurvivesTimeoutsAndStopsAsync(service, attempts);
    }

    [Fact]
    public async Task PriceArchiveKeepsRunningWhenTheBackfillTimesOut()
    {
        // Recording works; reading the backfill progress times out. That must neither end the loop nor mark the
        // backfill as done, so the second round tries it again.
        var attempts = new Attempts();
        using var services = new ServiceCollection()
            .AddSingleton(attempts)
            .AddScoped<IControllerSettingStore>(provider => new EmptySettings { TimeOutOnKey = ControllerSettingDefaults.PriceArchiveBackfilledThroughKey, Attempts = provider.GetRequiredService<Attempts>() })
            .AddScoped<IPublishedPriceArchive, UnusedArchive>()
            .AddScoped<IElectricityPriceForecastProvider, NoPrices>()
            .BuildServiceProvider();
        var service = new PublishedPriceArchiveBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), new Clock(),
            NullLogger<PublishedPriceArchiveBackgroundService>.Instance)
        { StartupDelay = TimeSpan.Zero, Interval = ShortPause, BackfillPause = TimeSpan.Zero };

        await AssertSurvivesTimeoutsAndStopsAsync(service, attempts);
    }

    [Fact]
    public async Task DecisionLogRetentionKeepsRunningAfterACancellationThatIsNotTheShutdown()
    {
        var attempts = new Attempts();
        using var services = TimingOutSettingsServices(attempts);
        var service = new DecisionLogRetentionBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), new Clock(),
            NullLogger<DecisionLogRetentionBackgroundService>.Instance)
        { StartupDelay = TimeSpan.Zero, Interval = ShortPause };

        await AssertSurvivesTimeoutsAndStopsAsync(service, attempts);
    }

    [Fact]
    public async Task EnergyHistoryKeepsRunningAfterACancellationThatIsNotTheShutdown()
    {
        var attempts = new Attempts();
        using var services = TimingOutSettingsServices(attempts);
        var service = new EnergyHistoryBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), new Clock(),
            NullLogger<EnergyHistoryBackgroundService>.Instance)
        { StartupDelay = TimeSpan.Zero, Interval = ShortPause };

        await AssertSurvivesTimeoutsAndStopsAsync(service, attempts);
    }

    [Fact]
    public async Task PriceForecastShadowReportsATimeoutAsProblemAndKeepsRunning()
    {
        var attempts = new Attempts();
        var state = new PriceForecastShadowState();
        using var services = new ServiceCollection()
            .AddSingleton(attempts)
            .AddScoped<IPriceForecastSource, TimingOutForecast>()
            .BuildServiceProvider();
        var service = new PriceForecastShadowService(services.GetRequiredService<IServiceScopeFactory>(), state, new Clock(),
            NullLogger<PriceForecastShadowService>.Instance)
        { StartupDelay = TimeSpan.Zero, Interval = ShortPause };

        await AssertSurvivesTimeoutsAndStopsAsync(service, attempts);

        Assert.Equal("Zeitüberschreitung beim Abruf der Preisprognose.", state.Status.Problem);
    }

    [Fact]
    public async Task CalendarSyncKeepsRunningAndStillReadsTheCalendarWhenTheRouteStepTimesOut()
    {
        var attempts = new Attempts();
        var calendarSettings = new CountingSettings();
        using var services = new ServiceCollection()
            .AddSingleton(attempts)
            // Stands for a route lookup that times out anywhere inside the trip planning.
            .AddScoped<EvTripPlanningService>(provider =>
            {
                provider.GetRequiredService<Attempts>().Count();
                throw new TaskCanceledException("Zeitüberschreitung im Test.");
            })
            .AddScoped(_ => new CalendarSyncRunner(null!, new CalendarSyncState(), calendarSettings, new Clock(), NullLogger<CalendarSyncRunner>.Instance))
            .BuildServiceProvider();
        var service = new CalendarSyncBackgroundService(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CalendarSyncBackgroundService>.Instance)
        { StartupDelay = TimeSpan.Zero, Interval = ShortPause };

        await AssertSurvivesTimeoutsAndStopsAsync(service, attempts);

        // The calendar step ran although the route step before it failed (the calendar is switched off here, so the
        // runner only read its switch).
        Assert.True(calendarSettings.Reads > 0);
    }

    [Fact]
    public async Task HagerPollingKeepsRunningWhenTheCloudTimesOut()
    {
        var attempts = new Attempts();
        using var services = new ServiceCollection()
            .AddSingleton(attempts)
            .AddScoped<Dal.HagerEnergy.HagerEnergyApiClient>(provider =>
            {
                provider.GetRequiredService<Attempts>().Count();
                throw new TaskCanceledException("Zeitüberschreitung im Test.");
            })
            .BuildServiceProvider();
        var service = new HagerEnergyPollingBackgroundService(services.GetRequiredService<IServiceScopeFactory>(),
            new Dal.HagerEnergy.HagerEnergyTelemetrySnapshotStore(), NullLogger<HagerEnergyPollingBackgroundService>.Instance);

        // The pause of this service is at least a minute, so only the first attempt is awaited.
        await AssertSurvivesTimeoutsAndStopsAsync(service, attempts, expectedAttempts: 1);
    }

    /// <summary>Starts the service, waits for the given number of timed-out attempts and checks that it still runs and that shutdown ends it.</summary>
    private static async Task AssertSurvivesTimeoutsAndStopsAsync(BackgroundService service, Attempts attempts, int expectedAttempts = 2)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            await attempts.ReachedAsync(expectedAttempts).WaitAsync(TimeSpan.FromSeconds(30));
            // Give a wrongly escaping exception the time to end ExecuteAsync.
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.NotNull(service.ExecuteTask);
            Assert.False(service.ExecuteTask.IsCompleted, "ExecuteAsync ended after a timeout; the host would stop the process.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.True(service.ExecuteTask!.IsCompleted);
        Assert.False(service.ExecuteTask.IsFaulted);
    }

    private static ServiceProvider TimingOutSettingsServices(Attempts attempts) => new ServiceCollection()
        .AddSingleton(attempts)
        .AddScoped<IControllerSettingStore>(provider => new EmptySettings { TimeOutAlways = true, Attempts = provider.GetRequiredService<Attempts>() })
        .BuildServiceProvider();

    /// <summary>Counts the failed attempts so a test can wait for the loop to come round again.</summary>
    private sealed class Attempts
    {
        private readonly object gate = new();
        private readonly List<(int Count, TaskCompletionSource Reached)> waiters = [];
        private int count;

        public void Count()
        {
            lock (gate)
            {
                count++;
                foreach (var waiter in waiters.Where(waiter => count >= waiter.Count)) waiter.Reached.TrySetResult();
            }
        }

        public Task ReachedAsync(int expected)
        {
            lock (gate)
            {
                if (count >= expected) return Task.CompletedTask;
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add((expected, reached));
                return reached.Task;
            }
        }
    }

    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => Now; }

    private sealed class EmptySettings : IControllerSettingStore
    {
        public bool TimeOutAlways { get; init; }
        public string? TimeOutOnKey { get; init; }
        public Attempts? Attempts { get; init; }

        public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default) =>
            TimeOutAlways ? throw TimedOut() : Task.FromResult<IReadOnlyList<ControllerSetting>>([]);

        public Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            TimeOutAlways || key == TimeOutOnKey ? throw TimedOut() : Task.FromResult<ControllerSetting?>(null);

        public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private TaskCanceledException TimedOut()
        {
            Attempts?.Count();
            return new TaskCanceledException("Zeitüberschreitung im Test.");
        }
    }

    private sealed class CountingSettings : IControllerSettingStore
    {
        private int reads;
        public int Reads => Volatile.Read(ref reads);

        public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ControllerSetting>>([]);

        public Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult<ControllerSetting?>(null);
        }

        public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TimingOutPrices(Attempts attempts) : IElectricityPriceForecastProvider
    {
        public string Source => "tibber";

        public Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            attempts.Count();
            // What HttpClient throws on its own timeout: a cancellation without the caller's token being cancelled.
            throw new TaskCanceledException("Zeitüberschreitung im Test.", new TimeoutException());
        }
    }

    private sealed class NoPrices : IElectricityPriceForecastProvider
    {
        public string Source => "tibber";

        public Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TibberPriceForecastSlot>>([]);
    }

    private sealed class UnusedArchive : IPublishedPriceArchive
    {
        public Task SaveAsync(string source, IReadOnlyList<TibberPriceForecastSlot> publishedSlots, DateTimeOffset recordedAtUtc,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<TibberPriceForecastSlot>> GetAsync(HistoricalPriceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TibberPriceForecastSlot>>([]);

        public Task<IReadOnlyList<TibberPriceForecastSlot>> GetAppliedAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, string currency,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TibberPriceForecastSlot>>([]);

        public Task RemoveOtherSuppliersFromAsync(string source, DateTimeOffset fromUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TimingOutForecast(Attempts attempts) : IPriceForecastSource
    {
        public Task<IReadOnlyList<PriceForecastPoint>?> GetAsync(CancellationToken cancellationToken = default)
        {
            attempts.Count();
            throw new TaskCanceledException("Zeitüberschreitung im Test.", new TimeoutException());
        }
    }
}
