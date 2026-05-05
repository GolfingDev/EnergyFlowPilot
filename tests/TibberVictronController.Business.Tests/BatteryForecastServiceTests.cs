using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Tests.TestDoubles;

namespace TibberVictronController.Business.Tests;

public sealed class BatteryForecastServiceTests
{
    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ForecastStartsAtUtc = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ForecastEndsAtUtc = ForecastStartsAtUtc.AddHours(1);

    [Fact]
    public async Task CalculateForecastAsyncLoadsInputsAndReturnsSimulatedForecast()
    {
        var settingsStore = await CreateSettingsStoreAsync(feedInCompensationPricePerKwh: "0.08");
        var service = new BatteryForecastService(
            new FakeTibberPriceForecastProvider(),
            new FakeWeatherForecastProvider(),
            new FakeHistoricalConsumptionProvider(),
            new FakeBatteryStateProvider(new BatteryState(55m, ForecastStartsAtUtc)),
            new FakeBatteryConfigurationProvider(new BatteryConfiguration(10m)),
            settingsStore);

        var result = await service.CalculateForecastAsync(ForecastStartsAtUtc, ForecastEndsAtUtc);

        Assert.Equal(55m, result.InitialBatteryState.StateOfChargePercent);
        Assert.Equal(4, result.Entries.Count);
        Assert.All(result.Entries, entry => Assert.True(entry.TimeSlot.IsFifteenMinuteSlot));
        Assert.All(result.Entries, entry => Assert.NotEmpty(entry.Reasons));
    }

    [Fact]
    public async Task CalculateForecastAsyncUsesConfiguredFeedInCompensation()
    {
        var settingsStore = await CreateSettingsStoreAsync(feedInCompensationPricePerKwh: "0.40");
        var service = new BatteryForecastService(
            new StaticTibberPriceForecastProvider(CreatePriceForecast(0.10m, -0.30m)),
            new StaticWeatherForecastProvider(CreatePvForecast(1.00m, 0m)),
            new StaticHistoricalConsumptionProvider(CreateConsumptionForecast(0.25m, 0m)),
            new FakeBatteryStateProvider(new BatteryState(80m, ForecastStartsAtUtc)),
            new FakeBatteryConfigurationProvider(new BatteryConfiguration(10m, maximumChargePowerWatts: 3000)),
            settingsStore);

        var result = await service.CalculateForecastAsync(ForecastStartsAtUtc, ForecastStartsAtUtc.AddMinutes(30));

        Assert.Equal(BatteryDecisionState.Charge, result.Entries[0].Decision.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.PV, result.Entries[0].Decision.Instruction.ChargeSource);
    }

    [Fact]
    public async Task CalculateForecastAsyncRejectsMissingFeedInCompensation()
    {
        var service = new BatteryForecastService(
            new FakeTibberPriceForecastProvider(),
            new FakeWeatherForecastProvider(),
            new FakeHistoricalConsumptionProvider(),
            new FakeBatteryStateProvider(),
            new FakeBatteryConfigurationProvider(),
            new FakeControllerSettingStore());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CalculateForecastAsync(ForecastStartsAtUtc, ForecastEndsAtUtc));

        Assert.Contains("Die Einspeiseverguetung ist nicht konfiguriert.", exception.Message);
    }

    [Fact]
    public async Task CalculateForecastAsyncRejectsInvalidFeedInCompensation()
    {
        var settingsStore = await CreateSettingsStoreAsync(feedInCompensationPricePerKwh: "ungueltig");
        var service = new BatteryForecastService(
            new FakeTibberPriceForecastProvider(),
            new FakeWeatherForecastProvider(),
            new FakeHistoricalConsumptionProvider(),
            new FakeBatteryStateProvider(),
            new FakeBatteryConfigurationProvider(),
            settingsStore);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CalculateForecastAsync(ForecastStartsAtUtc, ForecastEndsAtUtc));

        Assert.Contains("Die Einspeiseverguetung muss als Dezimalzahl konfiguriert sein.", exception.Message);
    }

    [Fact]
    public async Task CalculateForecastAsyncRejectsNonUtcRange()
    {
        var settingsStore = await CreateSettingsStoreAsync(feedInCompensationPricePerKwh: "0.08");
        var service = new BatteryForecastService(
            new FakeTibberPriceForecastProvider(),
            new FakeWeatherForecastProvider(),
            new FakeHistoricalConsumptionProvider(),
            new FakeBatteryStateProvider(),
            new FakeBatteryConfigurationProvider(),
            settingsStore);
        var localStart = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.FromHours(2));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CalculateForecastAsync(localStart, ForecastEndsAtUtc));

        Assert.Contains("Der Forecast-Start muss in UTC angegeben sein.", exception.Message);
    }

    [Fact]
    public async Task CalculateForecastAsyncLimitsForecastToAvailableTibberPrices()
    {
        var settingsStore = await CreateSettingsStoreAsync(feedInCompensationPricePerKwh: "0.08");
        var service = new BatteryForecastService(
            new StaticTibberPriceForecastProvider(CreatePriceForecast(0.20m, 0.21m)),
            new StaticWeatherForecastProvider(CreatePvForecast(0.10m, 0.10m)),
            new StaticHistoricalConsumptionProvider(CreateConsumptionForecast(0.20m, 0.20m)),
            new FakeBatteryStateProvider(new BatteryState(55m, ForecastStartsAtUtc)),
            new FakeBatteryConfigurationProvider(new BatteryConfiguration(10m)),
            settingsStore);

        var result = await service.CalculateForecastAsync(ForecastStartsAtUtc, ForecastStartsAtUtc.AddHours(24));

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(ForecastStartsAtUtc.AddMinutes(30), result.Entries[^1].TimeSlot.EndsAtUtc);
    }

    [Fact]
    public async Task CalculateForecastAsyncReturnsEmptyForecastWhenNoTibberPricesAreAvailable()
    {
        var settingsStore = await CreateSettingsStoreAsync(feedInCompensationPricePerKwh: "0.08");
        var service = new BatteryForecastService(
            new StaticTibberPriceForecastProvider(Array.Empty<TibberPriceForecastSlot>()),
            new ThrowingWeatherForecastProvider(),
            new ThrowingHistoricalConsumptionProvider(),
            new FakeBatteryStateProvider(new BatteryState(55m, ForecastStartsAtUtc)),
            new FakeBatteryConfigurationProvider(new BatteryConfiguration(10m)),
            settingsStore);

        var result = await service.CalculateForecastAsync(ForecastStartsAtUtc, ForecastStartsAtUtc.AddHours(24));

        Assert.Empty(result.Entries);
    }

    private static async Task<FakeControllerSettingStore> CreateSettingsStoreAsync(string feedInCompensationPricePerKwh)
    {
        var settingsStore = new FakeControllerSettingStore();
        var setting = new ControllerSetting(
            ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey,
            feedInCompensationPricePerKwh,
            ControllerSettingSensitivity.Normal,
            UpdatedAtUtc);

        await settingsStore.SaveSettingAsync(setting);

        return settingsStore;
    }

    private static IReadOnlyList<TibberPriceForecastSlot> CreatePriceForecast(params decimal[] prices)
    {
        return prices
            .Select((price, index) => new TibberPriceForecastSlot(CreateTimeSlot(index), price, "EUR"))
            .ToArray();
    }

    private static IReadOnlyList<PvYieldForecastSlot> CreatePvForecast(params decimal[] expectedPvYieldKwh)
    {
        return expectedPvYieldKwh
            .Select((pvYield, index) => new PvYieldForecastSlot(CreateTimeSlot(index), pvYield))
            .ToArray();
    }

    private static IReadOnlyList<ConsumptionForecastSlot> CreateConsumptionForecast(params decimal[] expectedConsumptionKwh)
    {
        return expectedConsumptionKwh
            .Select((consumption, index) => new ConsumptionForecastSlot(CreateTimeSlot(index), consumption))
            .ToArray();
    }

    private static ForecastTimeSlot CreateTimeSlot(int index)
    {
        var startsAtUtc = ForecastStartsAtUtc.AddMinutes(15 * index);

        return new ForecastTimeSlot(startsAtUtc, startsAtUtc.AddMinutes(15));
    }

    private sealed class StaticTibberPriceForecastProvider : ITibberPriceForecastProvider
    {
        private readonly IReadOnlyList<TibberPriceForecastSlot> priceForecast;

        public StaticTibberPriceForecastProvider(IReadOnlyList<TibberPriceForecastSlot> priceForecast)
        {
            this.priceForecast = priceForecast;
        }

        public Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(priceForecast);
        }
    }

    private sealed class StaticWeatherForecastProvider : IWeatherForecastProvider
    {
        private readonly IReadOnlyList<PvYieldForecastSlot> pvForecast;

        public StaticWeatherForecastProvider(IReadOnlyList<PvYieldForecastSlot> pvForecast)
        {
            this.pvForecast = pvForecast;
        }

        public Task<IReadOnlyList<PvYieldForecastSlot>> GetPvYieldForecastAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(pvForecast);
        }
    }

    private sealed class StaticHistoricalConsumptionProvider : IHistoricalConsumptionProvider
    {
        private readonly IReadOnlyList<ConsumptionForecastSlot> consumptionForecast;

        public StaticHistoricalConsumptionProvider(IReadOnlyList<ConsumptionForecastSlot> consumptionForecast)
        {
            this.consumptionForecast = consumptionForecast;
        }

        public Task<IReadOnlyList<ConsumptionForecastSlot>> GetConsumptionForecastAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(consumptionForecast);
        }
    }

    private sealed class ThrowingWeatherForecastProvider : IWeatherForecastProvider
    {
        public Task<IReadOnlyList<PvYieldForecastSlot>> GetPvYieldForecastAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Darf ohne Tibber-Preise nicht aufgerufen werden.");
        }
    }

    private sealed class ThrowingHistoricalConsumptionProvider : IHistoricalConsumptionProvider
    {
        public Task<IReadOnlyList<ConsumptionForecastSlot>> GetConsumptionForecastAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Darf ohne Tibber-Preise nicht aufgerufen werden.");
        }
    }
}
