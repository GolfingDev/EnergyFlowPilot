using System.Globalization;
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
    public async Task CalculateCurrentDecisionAsyncReturnsIdleWhenBatteryStateIsMissing()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new ThrowingBatteryStateProvider("Es liegt noch kein Live-Akkuladestand aus Victron MQTT vor."),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(500, 1200, NowUtc)),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.30m, 0.35m)),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Idle, result.Decision.Instruction.DecisionState);
        Assert.Contains(result.Reasons, reason => reason.RuleName == CurrentBatteryDecisionRuleIds.MissingBatteryState);
    }

    [Fact]
    public async Task CalculateCurrentDecisionAsyncReturnsIdleWhenSiteTelemetryIsMissing()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(55m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m)),
            CurrentSiteTelemetryProvider = new ThrowingCurrentSiteTelemetryProvider("Es liegt noch kein Live-Hausverbrauch aus Victron MQTT vor."),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.30m, 0.35m)),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Idle, result.Decision.Instruction.DecisionState);
        Assert.Contains(result.Reasons, reason => reason.RuleName == CurrentBatteryDecisionRuleIds.MissingSiteTelemetry);
    }

    [Fact]
    public async Task CalculateCurrentDecisionAsyncReturnsIdleWhenPriceLookupFails()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(55m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(500, 0, NowUtc)),
            TibberPriceForecastProvider = new ThrowingTibberPriceForecastProvider("Tibber API ist derzeit nicht erreichbar."),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Idle, result.Decision.Instruction.DecisionState);
        Assert.Contains(result.Reasons, reason => reason.RuleName == CurrentBatteryDecisionRuleIds.MissingCurrentPrice);
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
    public async Task CalculateCurrentDecisionAsyncUsesActiveManualGridCharge()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(60m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m, maximumChargePowerWatts: 3000)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(1200, 0, NowUtc)),
            TibberPriceForecastProvider = new ThrowingTibberPriceForecastProvider("Tibber darf fuer manuelle Ladung nicht gebraucht werden."),
            ControllerSettingStore = CreateSettingsStore(manualChargePowerWatts: 2500, manualChargeExpiresAtUtc: NowUtc.AddMinutes(30)),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Charge, result.Decision.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.Grid, result.Decision.Instruction.ChargeSource);
        Assert.Equal(2500, result.Decision.TargetPowerWatts);
        Assert.Contains(result.Reasons, reason => reason.RuleName == CurrentBatteryDecisionRuleIds.ManualGridCharge);
    }

    [Fact]
    public async Task CalculateCurrentDecisionAsyncCapsManualGridChargeToBatteryMaximum()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(60m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m, maximumChargePowerWatts: 3000)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(1200, 0, NowUtc)),
            TibberPriceForecastProvider = new ThrowingTibberPriceForecastProvider("Tibber darf fuer manuelle Ladung nicht gebraucht werden."),
            ControllerSettingStore = CreateSettingsStore(manualChargePowerWatts: 5000, manualChargeExpiresAtUtc: NowUtc.AddMinutes(30)),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Charge, result.Decision.Instruction.DecisionState);
        Assert.Equal(3000, result.Decision.TargetPowerWatts);
        Assert.Contains(result.Reasons, reason =>
            reason.RuleName == CurrentBatteryDecisionRuleIds.ManualGridCharge &&
            reason.Message.Contains("5000 Watt", StringComparison.Ordinal) &&
            reason.Message.Contains("3000 Watt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(0)]
    [InlineData(30)]
    public async Task CalculateCurrentDecisionAsyncIgnoresGridPowerInsideDeadband(int currentGridImportWatts)
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(60m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m, maximumChargePowerWatts: 3000, maximumDischargePowerWatts: 3000, roundTripEfficiencyPercent: 100m)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(currentGridImportWatts, 1200, NowUtc)),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.48m, 0.18m, 0.15m)),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Idle, result.Decision.Instruction.DecisionState);
        Assert.Equal(currentGridImportWatts, result.SiteTelemetry.CurrentGridImportWatts);
        Assert.Contains(result.Reasons, reason => reason.RuleName == CurrentBatteryDecisionRuleIds.GridPowerDeadband);
    }

    [Fact]
    public async Task CalculateCurrentDecisionAsyncDischargesAboveGridPowerDeadband()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(60m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m, maximumDischargePowerWatts: 3000, roundTripEfficiencyPercent: 100m)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(31, 0, NowUtc)),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.48m, 0.18m, 0.15m)),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Discharge, result.Decision.Instruction.DecisionState);
        Assert.Equal(31, result.Decision.TargetPowerWatts);
    }

    [Fact]
    public async Task CalculateCurrentDecisionAsyncDischargesAtNeutralPriceWhenForecastKeepsReserve()
    {
        var batteryConfiguration = new BatteryConfiguration(new BatteryConfigurationValues
        {
            TotalCapacityKwh = 12m,
            MinimumStateOfChargePercent = 10m,
            MaximumDischargePowerWatts = 3000,
            RoundTripEfficiencyPercent = 100m,
            TargetEndStateOfChargePercent = 25m,
            PlanningMinimumStateOfChargePercent = 15m,
            PlanningMaximumStateOfChargePercent = 95m
        });
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(95m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(batteryConfiguration),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(800, 0, NowUtc)),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.20m, 0.18m, 0.30m)),
            BatteryForecastService = new StaticBatteryForecastService(new BatteryForecastResult(
                new BatteryState(95m, NowUtc),
                batteryConfiguration,
                new[]
                {
                    CreateForecastEntry(
                        stateOfChargeBeforePercent: 95m,
                        stateOfChargeAfterPercent: 70m)
                })),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Discharge, result.Decision.Instruction.DecisionState);
        Assert.Equal(800, result.Decision.TargetPowerWatts);
        Assert.Contains(result.Reasons, reason => reason.RuleName == CurrentBatteryDecisionRuleIds.ForecastAllowsLoadCoverageDischarge);
    }

    [Fact]
    public async Task CalculateCurrentDecisionAsyncChargesFromPvAboveGridPowerDeadband()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(40m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m, maximumChargePowerWatts: 3000)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(-31, 1200, NowUtc)),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.30m, 0.18m, 0.15m)),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Charge, result.Decision.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.PV, result.Decision.Instruction.ChargeSource);
        Assert.Equal(31, result.Decision.TargetPowerWatts);
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

    [Fact]
    public async Task CalculateCurrentDecisionAsyncChargesFromPvExportWhenPriceLookupFails()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(40m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m, maximumChargePowerWatts: 3000)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(-1400, 2200, NowUtc)),
            TibberPriceForecastProvider = new ThrowingTibberPriceForecastProvider("Tibber API ist derzeit nicht erreichbar."),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Charge, result.Decision.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.PV, result.Decision.Instruction.ChargeSource);
        Assert.Equal(1400, result.Decision.TargetPowerWatts);
        Assert.Null(result.TibberPricePerKwh);
        Assert.Contains(result.Reasons, reason => reason.RuleName == CurrentBatteryDecisionRuleIds.AbsorbGridExport);
    }

    [Fact]
    public async Task CalculateCurrentDecisionAsyncLogsWhenPvChargeIsLimitedBySettings()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(40m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m, maximumChargePowerWatts: 2500)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(-4200, 5000, NowUtc)),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.30m, 0.18m, 0.15m)),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Charge, result.Decision.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.PV, result.Decision.Instruction.ChargeSource);
        Assert.Equal(2500, result.Decision.TargetPowerWatts);
        Assert.Contains(result.Reasons, reason =>
            reason.RuleName == CurrentBatteryDecisionRuleIds.ChargePowerLimitedBySettings &&
            reason.Message.Contains("4200 Watt", StringComparison.Ordinal) &&
            reason.Message.Contains("2500 Watt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CalculateCurrentDecisionAsyncDoesNotChargeFromPvAbovePlanningMaximum()
    {
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(99m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(new BatteryConfigurationValues
            {
                TotalCapacityKwh = 12m,
                MaximumChargePowerWatts = 3000,
                PlanningMaximumStateOfChargePercent = 95m
            })),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(-1400, 2200, NowUtc)),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.30m, 0.18m, 0.15m)),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = new FakeDecisionLogRepository()
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Idle, result.Decision.Instruction.DecisionState);
        Assert.Equal(0, result.Decision.TargetPowerWatts);
        Assert.Contains(result.Reasons, reason => reason.RuleName == BatteryForecastRuleIds.PlanningMaximumSocHeadroom);
    }

    [Fact]
    public async Task CalculateCurrentDecisionAsyncKeepsActivePvChargePowerInExportCalculation()
    {
        var decisionLogRepository = new FakeDecisionLogRepository();
        decisionLogRepository.SavedEntries.Add(CreateDecisionLogEntry(
            decidedAtUtc: NowUtc.AddMinutes(-1),
            validFromUtc: NowUtc.AddMinutes(-1),
            validToUtc: NowUtc.AddMinutes(54),
            decisionState: BatteryDecisionState.Charge,
            chargeSource: BatteryChargeSource.PV,
            targetPowerWatts: 5000));
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(40m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m, maximumChargePowerWatts: 5000)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(-3000, 8000, NowUtc)),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.30m, 0.18m, 0.15m)),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = decisionLogRepository
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Charge, result.Decision.Instruction.DecisionState);
        Assert.Equal(BatteryChargeSource.PV, result.Decision.Instruction.ChargeSource);
        Assert.Equal(5000, result.Decision.TargetPowerWatts);
        Assert.Equal(-8000, result.SiteTelemetry.CurrentGridImportWatts);
    }

    [Fact]
    public async Task CalculateCurrentDecisionAsyncDoesNotInferExportFromActivePvChargeWhenGridImports()
    {
        var decisionLogRepository = new FakeDecisionLogRepository();
        decisionLogRepository.SavedEntries.Add(CreateDecisionLogEntry(
            decidedAtUtc: NowUtc.AddMinutes(-1),
            validFromUtc: NowUtc.AddMinutes(-1),
            validToUtc: NowUtc.AddMinutes(54),
            decisionState: BatteryDecisionState.Charge,
            chargeSource: BatteryChargeSource.PV,
            targetPowerWatts: 476));
        var service = CreateService(new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = new FixedUtcClock(NowUtc),
            BatteryStateProvider = new FakeBatteryStateProvider(new BatteryState(40m, NowUtc)),
            BatteryConfigurationProvider = new FakeBatteryConfigurationProvider(new BatteryConfiguration(12m, maximumChargePowerWatts: 5000)),
            CurrentSiteTelemetryProvider = new FakeCurrentSiteTelemetryProvider(new CurrentSiteTelemetry(12, 0, NowUtc)),
            TibberPriceForecastProvider = new StaticTibberPriceForecastProvider(CreatePriceForecast(0.30m, 0.18m, 0.15m)),
            ControllerSettingStore = CreateSettingsStore(),
            DecisionLogRepository = decisionLogRepository
        });

        var result = await service.CalculateCurrentDecisionAsync();

        Assert.Equal(BatteryDecisionState.Idle, result.Decision.Instruction.DecisionState);
        Assert.Equal(0, result.Decision.TargetPowerWatts);
        Assert.Equal(12, result.SiteTelemetry.CurrentGridImportWatts);
        Assert.Contains(result.Reasons, reason => reason.RuleName == CurrentBatteryDecisionRuleIds.GridPowerDeadband);
    }

    private static CurrentBatteryDecisionService CreateService(CurrentBatteryDecisionServiceDependencies dependencies)
    {
        return new CurrentBatteryDecisionService(dependencies);
    }

    private static FakeControllerSettingStore CreateSettingsStore(
        int manualChargePowerWatts = 0,
        DateTimeOffset? manualChargeExpiresAtUtc = null)
    {
        var settingsStore = new FakeControllerSettingStore();
        settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey,
            "0.08",
            ControllerSettingSensitivity.Normal,
            NowUtc)).GetAwaiter().GetResult();
        settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.TelemetryGridPowerDeadbandWattsKey,
            "30",
            ControllerSettingSensitivity.Normal,
            NowUtc)).GetAwaiter().GetResult();
        settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.ManualChargePowerWattsKey,
            manualChargePowerWatts.ToString(CultureInfo.InvariantCulture),
            ControllerSettingSensitivity.Normal,
            NowUtc)).GetAwaiter().GetResult();
        settingsStore.SaveSettingAsync(new ControllerSetting(
            ControllerSettingDefaults.ManualChargeExpiresAtUtcKey,
            (manualChargeExpiresAtUtc ?? new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToString("O", CultureInfo.InvariantCulture),
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

    private static DecisionLogEntry CreateDecisionLogEntry(
        DateTimeOffset decidedAtUtc,
        DateTimeOffset validFromUtc,
        DateTimeOffset validToUtc,
        BatteryDecisionState decisionState,
        BatteryChargeSource? chargeSource,
        int targetPowerWatts)
    {
        return new DecisionLogEntry(
            Guid.NewGuid(),
            decidedAtUtc,
            validFromUtc,
            validToUtc,
            new CurrentBatteryDecision(new BatteryDecisionInstruction(decisionState, chargeSource), targetPowerWatts),
            stateOfChargePercent: 40m,
            tibberPricePerKwh: 0.30m,
            tibberPriceCurrency: "EUR",
            gridImportWatts: null,
            gridExportWatts: null,
            inputSummaryJson: "{}",
            new[] { new BatteryDecisionReason(CurrentBatteryDecisionRuleIds.AbsorbGridExport, "Testentscheidung.") });
    }

    private static BatteryForecastEntry CreateForecastEntry(
        decimal stateOfChargeBeforePercent,
        decimal stateOfChargeAfterPercent)
    {
        return new BatteryForecastEntry(
            new ForecastTimeSlot(NowUtc, NowUtc.AddHours(1)),
            TibberPricePerKwh: 0.20m,
            TibberPriceCurrency: "EUR",
            ExpectedPvYieldKwh: 0m,
            ExpectedConsumptionKwh: 0.80m,
            ExpectedGridImportBeforeBatteryKwh: 0.80m,
            stateOfChargeBeforePercent,
            stateOfChargeAfterPercent,
            new CurrentBatteryDecision(new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null), 0),
            new[] { new BatteryDecisionReason(BatteryForecastRuleIds.NeutralIdle, "Test-Forecast.") });
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

    private sealed class ThrowingCurrentSiteTelemetryProvider : ICurrentSiteTelemetryProvider
    {
        private readonly string message;

        public ThrowingCurrentSiteTelemetryProvider(string message)
        {
            this.message = message;
        }

        public Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(message);
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

        public Task<IReadOnlyList<DecisionLogEntry>> GetDecisionsAsync(
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            int maxCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DecisionLogEntry>>(SavedEntries
                .Where(entry => entry.DecidedAtUtc >= fromUtc && entry.DecidedAtUtc <= toUtc)
                .OrderBy(entry => entry.DecidedAtUtc)
                .Take(maxCount)
                .ToArray());
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

    private sealed class StaticBatteryForecastService : IBatteryForecastService
    {
        private readonly BatteryForecastResult forecastResult;

        public StaticBatteryForecastService(BatteryForecastResult forecastResult)
        {
            this.forecastResult = forecastResult;
        }

        public Task<BatteryForecastResult> CalculateForecastAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(forecastResult);
        }
    }

    private sealed class ThrowingTibberPriceForecastProvider : ITibberPriceForecastProvider
    {
        private readonly string message;

        public ThrowingTibberPriceForecastProvider(string message)
        {
            this.message = message;
        }

        public Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ThrowingBatteryStateProvider : IBatteryStateProvider
    {
        private readonly string message;

        public ThrowingBatteryStateProvider(string message)
        {
            this.message = message;
        }

        public Task<BatteryState> GetCurrentBatteryStateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(message);
        }
    }
}
