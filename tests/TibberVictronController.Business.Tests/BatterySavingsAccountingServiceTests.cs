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
    public async Task RefreshAsyncAccountsForTransitionsThroughZeroBatteryPower()
    {
        // Battery charges from grid, then a telemetry sample arrives at exactly 0 W (idle
        // transition), then it discharges. Before the fix, Math.Sign(0)==0 caused every pair
        // that touched a 0 W sample to be discarded, leaving net savings at zero.
        var liveRepository = new FakeLiveConsumptionRepository(new[]
        {
            CreateSample(0,  houseConsumptionWatts: 0m,    gridPowerWatts: 1000m, batteryPowerWatts: 1000m,  pvProductionWatts: 0m),
            CreateSample(5,  houseConsumptionWatts: 0m,    gridPowerWatts: 0m,    batteryPowerWatts: 0m,     pvProductionWatts: 0m),
            CreateSample(10, houseConsumptionWatts: 2000m, gridPowerWatts: 0m,    batteryPowerWatts: -2000m, pvProductionWatts: 0m),
            CreateSample(15, houseConsumptionWatts: 2000m, gridPowerWatts: 0m,    batteryPowerWatts: -2000m, pvProductionWatts: 0m)
        });
        var savingsRepository = new FakeBatterySavingsRepository();
        var service = CreateService(
            liveRepository,
            savingsRepository,
            new FakeTibberPriceForecastProvider(new[] { CreatePriceSlot(0, 30, 0.30m) }));

        await service.RefreshAsync(new BatterySavingsQuery
        {
            StartDate = new DateOnly(2026, 6, 4),
            EndDate = new DateOnly(2026, 6, 4),
            Currency = "EUR"
        });

        var summary = Assert.Single(savingsRepository.Summaries);
        Assert.Equal(0.0417m, summary.GridChargedEnergyKwh);
        Assert.Equal(0.0125m, summary.GridChargeCost);
        Assert.Equal(0.2500m, summary.DischargedEnergyKwh);
        Assert.Equal(0.0750m, summary.DischargeAvoidedCost);
        Assert.Equal(0.0625m, summary.NetSavings);
    }

    [Fact]
    public async Task RefreshAsyncDoesNotCountFeedInAndSupplementaryChargingAsSavings()
    {
        // 2000 W discharge, of which 500 W go into the grid and 1000 W charge the E3/DC battery without PV.
        var liveRepository = new FakeLiveConsumptionRepository(new[]
        {
            CreateSample(0, houseConsumptionWatts: 500m, gridPowerWatts: -500m, batteryPowerWatts: -2000m, pvProductionWatts: 0m),
            CreateSample(5, houseConsumptionWatts: 500m, gridPowerWatts: -500m, batteryPowerWatts: -2000m, pvProductionWatts: 0m)
        }.Select(sample => new LiveConsumptionSample(
            sample.HouseConsumptionWatts,
            sample.MeasuredAtUtc,
            sample.GridPowerWatts,
            sample.BatteryPowerWatts,
            sample.BatterySocPercent,
            sample.PvProductionWatts)
        {
            SupplementaryBatteryPowerWatts = 1000m,
            SupplementaryPvProductionWatts = 0m
        }).ToArray());
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

        var summary = Assert.Single(savingsRepository.Summaries);
        Assert.Equal(0.0417m, summary.DischargedEnergyKwh);
        Assert.Equal(0.0125m, summary.DischargeAvoidedCost);
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

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ProviderSwitchDoesNotRepriceOldOrMixedDays(int daysAfterSwitch, bool skipped)
    {
        var live = new FakeLiveConsumptionRepository([
            CreateSample(0, 500m, 0m, -1000m, 0m),
            CreateSample(5, 500m, 0m, -1000m, 0m)
        ]);
        var savings = new FakeBatterySavingsRepository();
        var settings = new TestDoubles.FakeControllerSettingStore();
        await settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey,
            "0.08", ControllerSettingSensitivity.Normal, UpdatedAtUtc));
        await settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.ElectricityPriceProviderChangedAtKey,
            StartsAtUtc.AddDays(-daysAfterSwitch).ToString("O"), ControllerSettingSensitivity.Normal, UpdatedAtUtc));
        var service = new BatterySavingsAccountingService(live, savings,
            new FakeTibberPriceForecastProvider([CreatePriceSlot(0, 15, 0.30m)]), settings, new FakeUtcClock());
        await service.RefreshAsync(new BatterySavingsQuery { StartDate = new(2026, 6, 4), EndDate = new(2026, 6, 4), Currency = "EUR" });
        Assert.Equal(skipped ? 0 : 1, savings.Summaries.Count);
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
