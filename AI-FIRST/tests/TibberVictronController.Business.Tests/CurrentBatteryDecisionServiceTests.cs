using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Tests.TestDoubles;

namespace TibberVictronController.Business.Tests;

public sealed class CurrentBatteryDecisionServiceTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 5, 2, 10, 5, 0, TimeSpan.Zero);

    [Fact]
    public async Task CalculateCurrentDecisionAsyncReturnsIdleForStaleSiteTelemetry()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(55m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(500, 1200, NowUtc.AddMinutes(-10))),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.30m, 0.35m)),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Idle, result.Decision.Instruction.DecisionState);
        Assert.Contains(result.Reasons, reason => reason.RuleName == CurrentBatteryDecisionRuleIds.StaleSiteTelemetry);
    }

    [Fact]
    public async Task CalculateCurrentDecisionAsyncDischargesOnlyUpToCurrentGridImport()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(60m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m, maximumDischargePowerWatts: 3000, roundTripEfficiencyPercent: 100m)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(1200, 0, NowUtc)),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.48m, 0.18m, 0.15m)),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Discharge, result.Decision.Instruction.DecisionState);
        Assert.Equal(1200, result.Decision.TargetPowerWatts);
    }

    [Fact]
    public async Task CalculateCurrentDecisionAsyncChargesFromPvWhenCurrentGridImportIsNegative()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(40m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m, maximumChargePowerWatts: 3000)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(-1400, 2200, NowUtc)),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.30m, 0.18m, 0.15m)),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Charge, result.Decision.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.PV, result.Decision.Instruction.ChargeSource);
        Assert.Equal(1400, result.Decision.TargetPowerWatts);
        Assert.Contains(result.Reasons, reason => reason.RuleName == CurrentBatteryDecisionRuleIds.AbsorbGridExport);
    }

    private static CurrentBatteryDecisionService CreateService(CurrentBatteryDecisionServiceDependencies dependencies)
    {
        return new CurrentBatteryDecisionService(dependencies);
    }

    private static FakeControllerSettingStore CreateSettingsStore()
    {
        var settingsStore = new FakeControllerSettingStore();
        settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey,
            "0.08",
            ControllerSettingSensitivity.Normal,
            NowUtc)).GetAwaiter().GetResult();

        return settingsStore;
    }

    private static IReadOnlyList<TibberPriceForecastSlot> CreatePriceForecast(params decimal[] prices)
    {
        return prices
            .Select((price, index) => new TibberPriceForecastSlot(
                new ForecastTimeSlot(NowUtc.AddHours(index), NowUtc.AddHours(index + 1)),
                price,
                "EUR"))
            .ToArray();
    }

    private sealed class FakeCurrentSiteTelemetryProvider : ICurrentSiteTelemetryProvider
    {
        private readonly CurrentSiteTelemetry siteTelemetry;

        public FakeCurrentSiteTelemetryProvider(CurrentSiteTelemetry siteTelemetry)
        {
            this.siteTelemetry = siteTelemetry;
        }

        public Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(siteTelemetry);
        }
    }

    private sealed class FakeDecisionLogRepository : IDecisionLogRepository
    {
        public List<DecisionLogEntry> SavedEntries { get; } = new();

        public Task SaveDecisionAsync(DecisionLogEntry decisionLogEntry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SavedEntries.Add(decisionLogEntry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DecisionLogEntry>> GetRecentDecisionsAsync(int maxCount, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DecisionLogEntry>>(SavedEntries.Take(maxCount).ToArray());
        }

        public Task<int> DeleteDecisionsOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }
    }

    private sealed class FixedUtcClock : IUtcClock
    {
        public FixedUtcClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
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
}
