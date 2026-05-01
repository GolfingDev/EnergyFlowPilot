using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class BatterySavingsCalculatorTests
{
    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 5, 1, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SlotStartsAtUtc = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CalculateDailySummariesUsesGridChargeCostAndDischargeAvoidedCost()
    {
        var calculator = new BatterySavingsCalculator();
        var movements = new[]
        {
            CreateMovement(new BatterySavingsSlotMovement
            {
                Instruction = new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
                TargetPowerWatts = 4000,
                TibberPricePerKwh = 0.10m
            }),
            CreateMovement(new BatterySavingsSlotMovement
            {
                Instruction = new BatteryDecisionInstruction(BatteryDecisionState.Discharge, chargeSource: null),
                TargetPowerWatts = 3000,
                TibberPricePerKwh = 0.40m
            })
        };

        var summaries = calculator.CalculateDailySummaries(movements, CreateOptions());

        var summary = Assert.Single(summaries);
        Assert.Equal(new DateOnly(2026, 5, 1), summary.AccountingDate);
        Assert.Equal(1.0000m, summary.GridChargedEnergyKwh);
        Assert.Equal(0.1000m, summary.GridChargeCost);
        Assert.Equal(0.7500m, summary.DischargedEnergyKwh);
        Assert.Equal(0.3000m, summary.DischargeAvoidedCost);
        Assert.Equal(0.2000m, summary.NetSavings);
        Assert.Equal(0.10m, summary.AverageGridChargePricePerKwh);
        Assert.Equal(0.40m, summary.AverageDischargePricePerKwh);
    }

    [Fact]
    public void CalculateDailySummariesUsesPvOpportunityPriceForPvSurplusCharging()
    {
        var calculator = new BatterySavingsCalculator();
        var movements = new[]
        {
            CreateMovement(new BatterySavingsSlotMovement
            {
                Instruction = new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.PV),
                TargetPowerWatts = 4000,
                TibberPricePerKwh = 0.30m,
                PvSalePricePerKwh = 0.08m
            }),
            CreateMovement(new BatterySavingsSlotMovement
            {
                Instruction = new BatteryDecisionInstruction(BatteryDecisionState.Discharge, chargeSource: null),
                TargetPowerWatts = 4000,
                TibberPricePerKwh = 0.35m
            })
        };

        var summaries = calculator.CalculateDailySummaries(movements, CreateOptions());

        var summary = Assert.Single(summaries);
        Assert.Equal(1.0000m, summary.PvChargedEnergyKwh);
        Assert.Equal(0.0800m, summary.PvOpportunityCost);
        Assert.Equal(1.0000m, summary.DischargedEnergyKwh);
        Assert.Equal(0.3500m, summary.DischargeAvoidedCost);
        Assert.Equal(0.2700m, summary.NetSavings);
        Assert.Equal(0.08m, summary.AveragePvOpportunityPricePerKwh);
    }

    [Fact]
    public void CalculateDailySummariesGroupsByEuropeBerlinAccountingDate()
    {
        var calculator = new BatterySavingsCalculator();
        var lateUtcSlot = new ForecastTimeSlot(
            new DateTimeOffset(2026, 5, 1, 22, 15, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 1, 22, 30, 0, TimeSpan.Zero));
        var movement = CreateMovement(new BatterySavingsSlotMovement
        {
            TimeSlot = lateUtcSlot,
            Instruction = new BatteryDecisionInstruction(BatteryDecisionState.Discharge, chargeSource: null),
            TargetPowerWatts = 4000,
            TibberPricePerKwh = 0.35m
        });

        var summaries = calculator.CalculateDailySummaries(new[] { movement }, CreateOptions());

        var summary = Assert.Single(summaries);
        Assert.Equal(new DateOnly(2026, 5, 2), summary.AccountingDate);
    }

    [Fact]
    public void BatterySavingsAggregateSumsDailyValuesAndWeightedPrices()
    {
        var summaries = new[]
        {
            CreateSummary(new BatterySavingsDailySummaryValues
            {
                AccountingDate = new DateOnly(2026, 5, 1),
                GridChargedEnergyKwh = 1m,
                GridChargeCost = 0.10m,
                DischargedEnergyKwh = 2m,
                DischargeAvoidedCost = 0.80m,
                NetSavings = 0.70m
            }),
            CreateSummary(new BatterySavingsDailySummaryValues
            {
                AccountingDate = new DateOnly(2026, 5, 2),
                GridChargedEnergyKwh = 3m,
                GridChargeCost = 0.90m,
                DischargedEnergyKwh = 1m,
                DischargeAvoidedCost = 0.50m,
                NetSavings = -0.40m
            })
        };

        var aggregate = BatterySavingsAggregate.FromDailySummaries(summaries);

        Assert.Equal(4m, aggregate.GridChargedEnergyKwh);
        Assert.Equal(1.00m, aggregate.GridChargeCost);
        Assert.Equal(3m, aggregate.DischargedEnergyKwh);
        Assert.Equal(1.30m, aggregate.DischargeAvoidedCost);
        Assert.Equal(0.30m, aggregate.NetSavings);
        Assert.Equal(0.25m, aggregate.AverageGridChargePricePerKwh);
        Assert.Equal(0.4333m, aggregate.AverageDischargePricePerKwh);
    }

    private static BatterySavingsCalculationOptions CreateOptions()
    {
        return new BatterySavingsCalculationOptions
        {
            ReportingTimeZone = ResolveBerlinTimeZone(),
            UpdatedAtUtc = UpdatedAtUtc
        };
    }

    private static BatterySavingsSlotMovement CreateMovement(BatterySavingsSlotMovement movement)
    {
        return movement with
        {
            TimeSlot = movement.TimeSlot ?? new ForecastTimeSlot(SlotStartsAtUtc, SlotStartsAtUtc.AddMinutes(15)),
            Currency = string.IsNullOrWhiteSpace(movement.Currency) ? "EUR" : movement.Currency,
            PvSalePricePerKwh = movement.PvSalePricePerKwh == 0m ? 0.08m : movement.PvSalePricePerKwh
        };
    }

    private static BatterySavingsDailySummary CreateSummary(BatterySavingsDailySummaryValues values)
    {
        var completeValues = new BatterySavingsDailySummaryValues
        {
            AccountingDate = values.AccountingDate,
            Currency = string.IsNullOrWhiteSpace(values.Currency) ? "EUR" : values.Currency,
            GridChargedEnergyKwh = values.GridChargedEnergyKwh,
            GridChargeCost = values.GridChargeCost,
            PvChargedEnergyKwh = values.PvChargedEnergyKwh,
            PvOpportunityCost = values.PvOpportunityCost,
            DischargedEnergyKwh = values.DischargedEnergyKwh,
            DischargeAvoidedCost = values.DischargeAvoidedCost,
            NetSavings = values.NetSavings,
            UpdatedAtUtc = UpdatedAtUtc
        };

        return new BatterySavingsDailySummary(completeValues);
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
