using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class BatterySavingsAccountingServiceTests
{
    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 6, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StartsAtUtc = new(2026, 6, 4, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RefreshAsyncPersistsDailySummaryFromMeasuredLiveBatteryPower()
    {
        var liveRepository = new FakeLiveConsumptionRepository(new[]
        {
            CreateSample(0, houseConsumptionWatts: 500m, gridPowerWatts: 500m, batteryPowerWatts: 1000m, pvProductionWatts: 0m),
            CreateSample(5, houseConsumptionWatts: 500m, gridPowerWatts: 500m, batteryPowerWatts: 1000m, pvProductionWatts: 0m),
            CreateSample(11, houseConsumptionWatts: 500m, gridPowerWatts: -1500m, batteryPowerWatts: 1000m, pvProductionWatts: 2500m),
            CreateSample(16, houseConsumptionWatts: 500m, gridPowerWatts: -1500m, batteryPowerWatts: 1000m, pvProductionWatts: 2500m),
            CreateSample(22, houseConsumptionWatts: 2000m, gridPowerWatts: 0m, batteryPowerWatts: -2000m, pvProductionWatts: 0m),
            CreateSample(27, houseConsumptionWatts: 2000m, gridPowerWatts: 0m, batteryPowerWatts: -2000m, pvProductionWatts: 0m)
        });
        var savingsRepository = new FakeBatterySavingsRepository();
        var service = CreateService(
            liveRepository,
            savingsRepository,
            new FakeTibberPriceForecastProvider(new[]
            {
                CreatePriceSlot(0, 15, 0.20m),
                CreatePriceSlot(15, 30, 0.30m)
            }));

        await service.RefreshAsync(new BatterySavingsQuery
        {
            StartDate = new DateOnly(2026, 6, 4),
            EndDate = new DateOnly(2026, 6, 4),
            Currency = "EUR"
        });

        var summary = Assert.Single(savingsRepository.Summaries);
        Assert.Equal(new DateOnly(2026, 6, 4), summary.AccountingDate);
        Assert.Equal(0.0833m, summary.GridChargedEnergyKwh);
        Assert.Equal(0.0167m, summary.GridChargeCost);
        Assert.Equal(0.0833m, summary.PvChargedEnergyKwh);
        Assert.Equal(0.0067m, summary.PvOpportunityCost);
        Assert.Equal(0.1667m, summary.DischargedEnergyKwh);
        Assert.Equal(0.0500m, summary.DischargeAvoidedCost);
        Assert.Equal(0.0267m, summary.NetSavings);
    }

    [Fact]
    public async Task RefreshAsyncSkipsTelemetryGaps()
    {
        var liveRepository = new FakeLiveConsumptionRepository(new[]
        {
            CreateSample(0, houseConsumptionWatts: 500m, gridPowerWatts: 0m, batteryPowerWatts: -2000m, pvProductionWatts: 0m),
            CreateSample(10, houseConsumptionWatts: 500m, gridPowerWatts: 0m, batteryPowerWatts: -2000m, pvProductionWatts: 0m)
        });
        var savingsRepository = new FakeBatterySavingsRepository();
        var service = CreateService(
            liveRepository,
            savingsRepository,
            new FakeTibberPriceForecastProvider(new[] { CreatePriceSlot(0, 15, 0.30m) }));

        await service.RefreshAsync(new BatterySavingsQuery
        {
            StartDate = new DateOnly(2026, 6, 4),
            EndDate = new DateOnly(2026, 6, 4),
            Currency = "EUR"
        });

        Assert.Empty(savingsRepository.Summaries);
    }

    private static BatterySavingsAccountingService CreateService(
        ILiveConsumptionRepository liveRepository,
        IBatterySavingsRepository savingsRepository,
        ITibberPriceForecastProvider tibberPriceForecastProvider)
    {
        return new BatterySavingsAccountingService(
            liveRepository,
            savingsRepository,
            tibberPriceForecastProvider,
            new FakeControllerSettingStore(),
            new FakeUtcClock());
    }

    private static LiveConsumptionSample CreateSample(
        int minutesAfterStart,
        decimal houseConsumptionWatts,
        decimal gridPowerWatts,
        decimal batteryPowerWatts,
        decimal pvProductionWatts)
    {
        return new LiveConsumptionSample(
            houseConsumptionWatts,
            StartsAtUtc.AddMinutes(minutesAfterStart),
            gridPowerWatts,
            batteryPowerWatts,
            batterySocPercent: null,
            pvProductionWatts);
    }

    private static TibberPriceForecastSlot CreatePriceSlot(int startsAfterMinutes, int endsAfterMinutes, decimal pricePerKwh)
    {
        return new TibberPriceForecastSlot(
            new ForecastTimeSlot(
                StartsAtUtc.AddMinutes(startsAfterMinutes),
                StartsAtUtc.AddMinutes(endsAfterMinutes)),
            pricePerKwh,
            "EUR");
    }

    private sealed class FakeLiveConsumptionRepository : ILiveConsumptionRepository
    {
        private readonly IReadOnlyList<LiveConsumptionSample> samples;

        public FakeLiveConsumptionRepository(IReadOnlyList<LiveConsumptionSample> samples)
        {
            this.samples = samples;
        }

        public Task SaveSampleAsync(LiveConsumptionSample sample, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<LiveConsumptionSample>> GetSamplesAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var samplesInRange = samples
                .Where(sample => sample.MeasuredAtUtc >= startsAtUtc && sample.MeasuredAtUtc < endsAtUtc)
                .OrderBy(sample => sample.MeasuredAtUtc)
                .ToArray();

            return Task.FromResult<IReadOnlyList<LiveConsumptionSample>>(samplesInRange);
        }

        public Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeBatterySavingsRepository : IBatterySavingsRepository
    {
        public List<BatterySavingsDailySummary> Summaries { get; } = new();

        public Task SaveDailySummaryAsync(
            BatterySavingsDailySummary summary,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Summaries.RemoveAll(existing =>
                existing.AccountingDate == summary.AccountingDate &&
                existing.Currency == summary.Currency);
            Summaries.Add(summary);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BatterySavingsDailySummary>> GetDailySummariesAsync(
            BatterySavingsQuery query,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<BatterySavingsAggregate> GetAggregateAsync(
            BatterySavingsQuery query,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeTibberPriceForecastProvider : ITibberPriceForecastProvider
    {
        private readonly IReadOnlyList<TibberPriceForecastSlot> prices;

        public FakeTibberPriceForecastProvider(IReadOnlyList<TibberPriceForecastSlot> prices)
        {
            this.prices = prices;
        }

        public Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(prices);
        }
    }

    private sealed class FakeControllerSettingStore : IControllerSettingStore
    {
        public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ControllerSetting? setting = key == ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey
                ? new ControllerSetting(key, "0.08", ControllerSettingSensitivity.Normal, UpdatedAtUtc)
                : null;

            return Task.FromResult(setting);
        }

        public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeUtcClock : IUtcClock
    {
        public DateTimeOffset UtcNow => UpdatedAtUtc;
    }
}
