using TibberVictronController.Business.Dashboard;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class DashboardSummaryServiceTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(3030, -774, DashboardCheckSeverity.Critical)]
    [InlineData(3030, 0, DashboardCheckSeverity.Ok)]
    [InlineData(-3030, -774, DashboardCheckSeverity.Ok)]
    public void MonitoringDetectsOppositeBatteryTransferDirection(int battery, int secondary, DashboardCheckSeverity expected)
    {
        var result = DashboardSummaryService.CheckSupplementarySupply(CreateSample(StartUtc, 27, battery, 79, secondary, 3090));
        Assert.Equal("battery-from-supplementary", result.CheckId);
        Assert.Equal(expected, result.Severity);
    }

    [Fact]
    public void CalculateReviewDetectsFeedInAndSupplementaryChargingFromBattery()
    {
        // Pattern of 2026-10-14 23:20: Victron discharges 4.8 kW, 300 W go into the grid and the E3/DC charges 2 kW without PV.
        var samples = Enumerable.Range(0, 3)
            .Select(index => CreateSample(StartUtc.AddMinutes(index), gridWatts: -300m, batteryWatts: -4800m, socPercent: 50m, supplementaryBatteryWatts: 2000m, supplementaryPvWatts: 0m))
            .ToArray();

        var review = DashboardSummaryService.CalculateReview(StartUtc, StartUtc.AddMinutes(3), samples, planningMinimumStateOfChargePercent: 15m);

        Assert.Equal(0.160m, review.BatteryDischargeKwh);
        Assert.Equal(0.010m, review.BatteryFeedInKwh);
        Assert.Equal(0.067m, review.SupplementaryChargedFromBatteryKwh);
        Assert.Equal(0m, review.GridChargeKwh);
        Assert.Single(review.Hours);
    }

    [Fact]
    public void CalculateReviewIgnoresSupplementaryChargingFromOwnPv()
    {
        var samples = Enumerable.Range(0, 3)
            .Select(index => CreateSample(StartUtc.AddMinutes(index), gridWatts: 0m, batteryWatts: -1000m, socPercent: 50m, supplementaryBatteryWatts: 2000m, supplementaryPvWatts: 3000m))
            .ToArray();

        var review = DashboardSummaryService.CalculateReview(StartUtc, StartUtc.AddMinutes(3), samples, planningMinimumStateOfChargePercent: 15m);

        Assert.Equal(0m, review.SupplementaryChargedFromBatteryKwh);
    }

    [Fact]
    public void CalculateReviewCapsSampleGapsAndCountsImportOnlyWhileBatteryHasEnergy()
    {
        var samples = new[]
        {
            CreateSample(StartUtc, gridWatts: 600m, batteryWatts: 0m, socPercent: 50m),
            CreateSample(StartUtc.AddMinutes(30), gridWatts: 600m, batteryWatts: 0m, socPercent: 15m),
            CreateSample(StartUtc.AddMinutes(31), gridWatts: 600m, batteryWatts: 0m, socPercent: 15m)
        };

        var review = DashboardSummaryService.CalculateReview(StartUtc, StartUtc.AddMinutes(31), samples, planningMinimumStateOfChargePercent: 15m);

        Assert.Equal(0.020m, review.GridImportKwh);
        Assert.Equal(0.010m, review.GridImportWhileBatteryAvailableKwh);
    }

    [Fact]
    public void CalculateReviewCountsGridChargeOnlyWhileImporting()
    {
        var samples = new[]
        {
            CreateSample(StartUtc, gridWatts: 3000m, batteryWatts: 2500m, socPercent: 50m),
            CreateSample(StartUtc.AddMinutes(1), gridWatts: -500m, batteryWatts: 2500m, socPercent: 51m),
            CreateSample(StartUtc.AddMinutes(2), gridWatts: -500m, batteryWatts: 2500m, socPercent: 52m)
        };

        var review = DashboardSummaryService.CalculateReview(StartUtc, StartUtc.AddMinutes(2), samples, planningMinimumStateOfChargePercent: 15m);

        Assert.Equal(0.042m, review.GridChargeKwh);
    }

    [Theory]
    [InlineData(BatteryDecisionState.Charge, BatteryChargeSource.Grid, null, DashboardActionCategory.GridCharge)]
    [InlineData(BatteryDecisionState.Charge, BatteryChargeSource.PV, null, DashboardActionCategory.PvCharge)]
    [InlineData(BatteryDecisionState.Discharge, null, BatteryForecastRuleIds.SelfConsumptionDischarge, DashboardActionCategory.Discharge)]
    [InlineData(BatteryDecisionState.Idle, null, BatteryForecastRuleIds.HoldForExpensiveSlot, DashboardActionCategory.Hold)]
    [InlineData(BatteryDecisionState.Idle, null, BatteryForecastRuleIds.PlanningMaximumSocHeadroom, DashboardActionCategory.Full)]
    [InlineData(BatteryDecisionState.Idle, null, "GRID_POWER_DEADBAND", DashboardActionCategory.Idle)]
    public void CategorizeMapsDecisionsToDashboardCategories(
        BatteryDecisionState state,
        BatteryChargeSource? chargeSource,
        string? ruleId,
        DashboardActionCategory expectedCategory)
    {
        var category = DashboardSummaryService.Categorize(new BatteryDecisionInstruction(state, chargeSource), ruleId);

        Assert.Equal(expectedCategory, category);
    }

    private static LiveConsumptionSample CreateSample(
        DateTimeOffset measuredAtUtc,
        decimal gridWatts,
        decimal batteryWatts,
        decimal socPercent,
        decimal? supplementaryBatteryWatts = null,
        decimal? supplementaryPvWatts = null)
    {
        return new LiveConsumptionSample(1000m, measuredAtUtc, gridWatts, batteryWatts, socPercent, pvProductionWatts: null)
        {
            SupplementaryBatteryPowerWatts = supplementaryBatteryWatts,
            SupplementaryBatterySocPercent = supplementaryBatteryWatts is null ? null : 60m,
            SupplementaryPvProductionWatts = supplementaryPvWatts
        };
    }
}
