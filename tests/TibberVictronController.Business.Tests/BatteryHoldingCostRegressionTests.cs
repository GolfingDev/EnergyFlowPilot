using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class BatteryHoldingCostRegressionTests
{
    [Fact]
    public void October21MiddayChargesForTheMoreExpensiveEveningDespiteIntermediateConsumption()
    {
        // 21.10.2026, 12:15–17:00 Berlin: published prices and rounded net-load forecast from server diagnosis.
        // The old per-slot hold penalty rejected every grid charge, even though 37.62 / .90 < 46.54 ct/kWh.
        decimal[] prices = [.3762m, .3825m, .3930m, .3911m, .3954m, .3954m, .3894m, .3871m, .3924m, .3990m,
            .4132m, .3970m, .4124m, .4331m, .4491m, .4266m, .4470m, .4543m, .4605m, .4654m];
        decimal[] loads = [.300m, .163m, .137m, .011m, .060m, .151m, .177m, .242m, .380m, .320m,
            .351m, .294m, .185m, .204m, .111m, .114m, .168m, .264m, .139m, .354m];
        var result = Simulate(prices, loads);
        Assert.Equal(BatteryChargeSource.Grid, result.Entries[0].Decision.Instruction.ChargeSource);
        Assert.Equal(BatteryDecisionState.Discharge, result.Entries[^1].Decision.Instruction.DecisionState);
        Assert.True(result.Entries[^1].StateOfChargeBeforePercent > 15m);
        AssertFinancialAndPhysicalLimits(result, prices, loads);
    }

    [Fact]
    public void October21NightCanChargeForMorningPeak()
    {
        var prices = Enumerable.Repeat(.36m, 19).ToArray();
        prices[0] = .3438m; // 02:15 purchase
        prices[^1] = .4669m; // 06:45 consumption
        var loads = Enumerable.Repeat(.25m, prices.Length).ToArray();
        var result = Simulate(prices, loads);
        Assert.Equal(BatteryChargeSource.Grid, result.Entries[0].Decision.Instruction.ChargeSource);
        Assert.Equal(BatteryDecisionState.Discharge, result.Entries[^1].Decision.Instruction.DecisionState);
        AssertFinancialAndPhysicalLimits(result, prices, loads);
    }

    [Fact]
    public void October21At1500TheSpreadNoLongerCoversLossesAndMinimumSavings()
    {
        decimal[] prices = [.3970m, .4124m, .4331m, .4491m, .4266m, .4470m, .4543m, .4605m, .4654m];
        var result = Simulate(prices, Enumerable.Repeat(.25m, prices.Length).ToArray());
        Assert.DoesNotContain(result.Entries, e => e.Decision.Instruction.ChargeSource == BatteryChargeSource.Grid);
    }

    [Theory]
    [InlineData(1, .24, true)]
    [InlineData(4, .24, true)]
    [InlineData(20, .24, true)]
    [InlineData(84, .24, true)]
    [InlineData(1, .22, false)]
    [InlineData(20, .22, false)]
    public void ExistingEnergyPaysTheMinimumHoldAdvantageOnceRegardlessOfWaitingSlots(
        int waitingSlots, double laterPrice, bool shouldHold)
    {
        var prices = Enumerable.Repeat(.20m, waitingSlots).Append((decimal)laterPrice).ToArray();
        var loads = Enumerable.Repeat(.25m, prices.Length).ToArray();
        // Grid purchases in the waiting slots are forbidden, isolating deferral of existing energy.
        var result = Simulate(prices, loads, initialSoc: 17.5m, capacity: 10m, efficiency: 100m,
            estimatedWaitingSlots: waitingSlots);
        Assert.Equal(shouldHold ? BatteryDecisionState.Idle : BatteryDecisionState.Discharge,
            result.Entries[0].Decision.Instruction.DecisionState);
        Assert.Equal(shouldHold ? BatteryDecisionState.Discharge : BatteryDecisionState.Idle,
            result.Entries[^1].Decision.Instruction.DecisionState);
        Assert.DoesNotContain(result.Entries, e => e.Decision.Instruction.ChargeSource == BatteryChargeSource.Grid);
        Assert.All(result.Entries, e => Assert.InRange(e.StateOfChargeAfterPercent, 15m, 95m));
    }

    private static BatteryForecastResult Simulate(decimal[] prices, decimal[] loads,
        decimal initialSoc = 15m, decimal capacity = 19.2m, decimal efficiency = 90m, int estimatedWaitingSlots = 0)
    {
        var start = new DateTimeOffset(2026, 10, 21, 10, 15, 0, TimeSpan.Zero);
        var slots = Enumerable.Range(0, prices.Length)
            .Select(i => new ForecastTimeSlot(start.AddMinutes(15 * i), start.AddMinutes(15 * (i + 1)))).ToArray();
        var configuration = new BatteryConfiguration(new BatteryConfigurationValues
        {
            TotalCapacityKwh = capacity, MinimumStateOfChargePercent = 10m,
            PlanningMinimumStateOfChargePercent = 15m, PlanningMaximumStateOfChargePercent = 95m,
            TargetEndStateOfChargePercent = 25m, MaximumChargePowerWatts = 4000,
            MaximumDischargePowerWatts = 4000, RoundTripEfficiencyPercent = efficiency,
            MinimumSavingsPerKwh = .03m, WearCostPerKwh = 0m
        });
        return new BatteryForecastSimulator().Simulate(
            slots.Select((s, i) => new TibberPriceForecastSlot(s, prices[i], "EUR")
                { IsEstimated = i < estimatedWaitingSlots }).ToArray(),
            slots.Select(s => new PvYieldForecastSlot(s, 0m)).ToArray(),
            slots.Select((s, i) => new ConsumptionForecastSlot(s, loads[i])).ToArray(),
            new BatteryState(initialSoc, start), configuration, .08m);
    }

    private static void AssertFinancialAndPhysicalLimits(BatteryForecastResult result, decimal[] prices, decimal[] loads)
    {
        decimal baselineCost = 0m, plannedCost = 0m, gridChargeKwh = 0m;
        for (var i = 0; i < prices.Length; i++)
        {
            var entry = result.Entries[i];
            var energy = entry.Decision.TargetPowerWatts / 1000m * .25m;
            var netImport = loads[i];
            if (entry.Decision.Instruction.ChargeSource == BatteryChargeSource.Grid)
            { netImport += energy; gridChargeKwh += energy; }
            else if (entry.Decision.Instruction.DecisionState == BatteryDecisionState.Discharge)
                netImport -= energy;
            baselineCost += loads[i] * prices[i];
            plannedCost += netImport * prices[i];
            Assert.True(netImport >= -.001m); // no battery feed-in; account for integer-watt rounding
            Assert.InRange(entry.Decision.TargetPowerWatts, 0, 4000);
            Assert.InRange(entry.StateOfChargeAfterPercent, 15m, 95m);
        }
        Assert.True(gridChargeKwh > 0m);
        Assert.True(baselineCost - plannedCost > .03m * gridChargeKwh - .001m,
            $"Netzladung muss nach Verlusten den Mindestvorteil erreichen: Vorteil {baselineCost - plannedCost}, Netzladung {gridChargeKwh} kWh.");
    }
}
