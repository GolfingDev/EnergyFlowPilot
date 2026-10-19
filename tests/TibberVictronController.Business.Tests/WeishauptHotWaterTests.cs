using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Business.Tests;

public sealed class WeishauptHotWaterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly WeishauptHotWaterSettings Settings = new() { IsEnabled = true };

    [Theory]
    [InlineData(0.19, false, true)]
    [InlineData(0.21, false, false)]
    [InlineData(0.21, true, true)]
    [InlineData(0.23, true, false)]
    public void CurrentPriceUsesRelativeAdvantageAndHysteresis(decimal price, bool raised, bool expected)
    {
        var slot = Price(price);
        Assert.Equal(expected, WeishauptHotWaterPolicy.ShouldBoost(Settings, Now, raised, slot, null, Plan()));
        Assert.False(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now, raised, slot with { IsEstimated = true }, null, Plan()));
    }

    [Fact]
    public void ProductionWithoutExportAndBatteryExportDoNotAuthorizeBoost()
    {
        var export = new CurrentSiteTelemetry(-2500, 4000, Now, 0);
        Assert.True(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now, false, null, export));
        Assert.False(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now, false, null, new CurrentSiteTelemetry(500, 4000, Now, 0)));
        Assert.False(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now, false, null, export with { SupplementaryBatteryDischargeWatts = 1000 }));
        Assert.False(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now, false, null, new CurrentSiteTelemetry(-2500, 4000, Now, -1000)));
        Assert.False(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now.AddMinutes(3), false, null, export));
        Assert.False(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now.AddSeconds(-1), false, null, export));
        Assert.False(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now, false, null, new CurrentSiteTelemetry(-2500, 4000, Now)));
        Assert.True(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now, false, Price(0.10m), null, Plan()));
        Assert.False(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now, false, Price(0.10m), null));
    }

    [Fact]
    public async Task BoostIsWrittenOnceAndRestoredAfterSignalEndsAcrossServiceRestart()
    {
        var device = new Device();
        var store = new Store();
        var events = new Events();
        var service = new WeishauptHotWaterControlService(device, store, events);
        await service.RunAsync(true, true, Now, default);
        Assert.Equal(45m, store.State.OriginalCelsius);
        Assert.Equal(50m, device.Temperature);
        await service.RunAsync(true, true, Now.AddMinutes(1), default);
        await service.RunAsync(true, false, Now.AddMinutes(2), default);
        Assert.Single(device.Writes);
        service = new WeishauptHotWaterControlService(device, store, events);
        await service.RunAsync(true, false, Now.AddMinutes(11), default);
        Assert.Equal(new decimal[] { 50m, 45m }, device.Writes);
        Assert.Null(store.State.OriginalCelsius);
        await service.RunAsync(true, true, Now.AddMinutes(12), default);
        Assert.Equal(2, device.Writes.Count);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 241)]
    public async Task DisableAndMaximumDurationRestoreEvenWhenMinimumHoldOrSignalWouldKeepBoost(bool enabled, bool boost, int minutes)
    {
        var device = new Device(); var store = new Store(); var events = new Events();
        var service = new WeishauptHotWaterControlService(device, store, events);
        await service.RunAsync(true, true, Now, default);
        await service.RunAsync(enabled, boost, Now.AddMinutes(minutes), default);
        Assert.Equal(45m, device.Temperature);
        Assert.Null(store.State.OriginalCelsius);
    }

    [Fact]
    public async Task ManualChangeIsNotOverwrittenAndSuspendsUntilDisabled()
    {
        var device = new Device(); var store = new Store(); var events = new Events();
        var service = new WeishauptHotWaterControlService(device, store, events);
        await service.RunAsync(true, true, Now, default);
        device.Temperature = 47m;
        await service.RunAsync(true, true, Now.AddMinutes(12), default);
        await service.RunAsync(true, true, Now.AddHours(1), default);
        Assert.True(store.State.Suspended);
        Assert.Single(device.Writes);
        await service.RunAsync(false, false, Now.AddHours(2), default);
        Assert.False(store.State.Suspended);
        Assert.Equal(47m, device.Temperature);
    }

    [Fact]
    public async Task LostWriteAcknowledgementKeepsOriginalForRecovery()
    {
        var device = new Device { LoseAcknowledgement = true }; var store = new Store(); var events = new Events();
        var service = new WeishauptHotWaterControlService(device, store, events);
        await Assert.ThrowsAsync<IOException>(() => service.RunAsync(true, true, Now, default));
        Assert.Equal(45m, store.State.OriginalCelsius);
        Assert.Equal(50m, device.Temperature);
        device.LoseAcknowledgement = false;
        await service.RunAsync(false, false, Now.AddMinutes(1), default);
        Assert.Equal(45m, device.Temperature);
    }

    [Fact]
    public async Task FailedAuditPreventsHardwareWriteAndDisabledIdleDoesNotConnect()
    {
        var device = new Device(); var store = new Store(); var events = new Events { Fail = true };
        var service = new WeishauptHotWaterControlService(device, store, events);
        await service.RunAsync(false, false, Now, default);
        Assert.Equal(0, device.Reads);
        await Assert.ThrowsAsync<IOException>(() => service.RunAsync(true, true, Now, default));
        Assert.Empty(device.Writes);
    }

    private static TibberPriceForecastSlot Price(decimal value) => new(new ForecastTimeSlot(Now.AddMinutes(-1), Now.AddMinutes(14)), value, "EUR");

    private static WeishauptHotWaterPricePlan Plan() => new(Now.AddHours(6), Now, Now.AddHours(1), 0.19m, 0.252m, 0.0335m, 6m, 60, "Preisfenster");

    [Fact]
    public async Task PlannedWindowEndsOwnedBoostAndKeepsEpisodeAfterRestart()
    {
        var device = new Device(); var store = new Store(); var events = new Events();
        var context = new WeishauptHotWaterBoostContext(Plan(), "Preisfenster", LatestTemperatureCelsius: 44m);
        var service = new WeishauptHotWaterControlService(device, store, events);
        await service.RunAsync(true, true, Now, default, context);
        Assert.True(Assert.Single(store.State.Episodes).WriteConfirmed);
        service = new WeishauptHotWaterControlService(device, store, events);
        await service.RunAsync(true, true, Now.AddHours(1), default, context);
        Assert.Equal(45m, device.Temperature);
        Assert.Equal(Now.AddHours(1), Assert.Single(store.State.Episodes).EndedAtUtc);
        Assert.Null(store.State.OriginalCelsius);
    }

    [Fact]
    public async Task TargetReachedRestoresWithoutWaitingForPriceWindowEnd()
    {
        var device = new Device(); var store = new Store(); var events = new Events();
        var service = new WeishauptHotWaterControlService(device, store, events);
        var context = new WeishauptHotWaterBoostContext(Plan(), "Preisfenster", LatestTemperatureCelsius: 44m);
        await service.RunAsync(true, true, Now, default, context);
        await service.RunAsync(true, true, Now.AddMinutes(20), default, context with { TargetReached = true });
        Assert.Equal(45m, device.Temperature);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-3276.8")]
    [InlineData("50")]
    public async Task MissingInvalidOrAlreadyHotTemperatureDoesNotStartBoost(string? actualValue)
    {
        decimal? actual = actualValue is null ? null : decimal.Parse(actualValue, System.Globalization.CultureInfo.InvariantCulture);
        var device = new Device(); var store = new Store(); var events = new Events();
        await new WeishauptHotWaterControlService(device, store, events).RunAsync(true, true, Now, default,
            new WeishauptHotWaterBoostContext(Plan(), "Preisfenster", LatestTemperatureCelsius: actual));
        Assert.Empty(device.Writes);
        Assert.Empty(store.State.Episodes);
    }

    private sealed class Device : IWeishauptHotWaterControl
    {
        public decimal Temperature = 45m;
        public bool LoseAcknowledgement;
        public int Reads;
        public List<decimal> Writes = [];
        public Task<WeishauptHotWaterReading> ReadAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(new WeishauptHotWaterReading("device", Temperature));
        }
        public Task SetNormalTemperatureAsync(WeishauptHotWaterReading expected, decimal target, CancellationToken ct)
        {
            Assert.Equal(expected.NormalSetPointCelsius, Temperature);
            Writes.Add(target); Temperature = target;
            if (LoseAcknowledgement) throw new IOException("Verbindung unterbrochen.");
            return Task.CompletedTask;
        }
    }

    private sealed class Store : IWeishauptHotWaterStateStore
    {
        public WeishauptHotWaterState State = new();
        public Task<WeishauptHotWaterState> LoadAsync(CancellationToken ct) => Task.FromResult(State);
        public Task SaveAsync(WeishauptHotWaterState state, CancellationToken ct) { State = state; return Task.CompletedTask; }
    }

    private sealed class Events : IOperationalEventRepository
    {
        public bool Fail;
        public Task SaveEventAsync(OperationalEvent value, CancellationToken ct) => Fail ? throw new IOException("Protokollspeicher nicht verfuegbar.") : Task.CompletedTask;
        public Task<IReadOnlyList<OperationalEvent>> GetRecentEventsAsync(int count, CancellationToken ct) => throw new NotSupportedException();
    }
}
