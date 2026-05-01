using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Auditing;

/// <summary>
/// Provides a naive self-consumption baseline without Tibber price optimization.
/// </summary>
public sealed class BaselineDecisionEngine
{
    private const string RuleId = "BaselineSelfConsumption";

    /// <summary>
    /// Simulates a baseline that charges PV surplus and discharges only to cover household import.
    /// </summary>
    public IReadOnlyList<BatteryForecastEntry> Simulate(DecisionAuditScenario scenario)
    {
        var pvSlotsByTime = scenario.PvForecast.ToDictionary(slot => slot.TimeSlot);
        var consumptionSlotsByTime = scenario.ConsumptionForecast.ToDictionary(slot => slot.TimeSlot);
        var batteryEnergyKwh = scenario.BatteryConfiguration.TotalCapacityKwh *
            scenario.InitialBatteryState.StateOfChargePercent / 100m;
        var entries = new List<BatteryForecastEntry>();

        foreach (var priceSlot in scenario.PriceForecast.OrderBy(slot => slot.TimeSlot.StartsAtUtc))
        {
            var pvSlot = pvSlotsByTime[priceSlot.TimeSlot];
            var consumptionSlot = consumptionSlotsByTime[priceSlot.TimeSlot];
            var stateOfChargeBeforePercent = CalculateStateOfChargePercent(batteryEnergyKwh, scenario.BatteryConfiguration);
            var gridImportBeforeBatteryKwh = consumptionSlot.ExpectedConsumptionKwh - pvSlot.ExpectedPvYieldKwh;
            var simulatedSlot = SimulateSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                stateOfChargeBeforePercent,
                batteryEnergyKwh,
                gridImportBeforeBatteryKwh,
                scenario.BatteryConfiguration);

            batteryEnergyKwh = simulatedSlot.BatteryEnergyAfterKwh;
            entries.Add(simulatedSlot.Entry);
        }

        return entries;
    }

    private static SimulatedBaselineSlot SimulateSlot(
        TibberPriceForecastSlot priceSlot,
        PvYieldForecastSlot pvSlot,
        ConsumptionForecastSlot consumptionSlot,
        decimal stateOfChargeBeforePercent,
        decimal batteryEnergyBeforeKwh,
        decimal gridImportBeforeBatteryKwh,
        BatteryConfiguration batteryConfiguration)
    {
        var minimumBatteryEnergyKwh = batteryConfiguration.TotalCapacityKwh *
            batteryConfiguration.MinimumStateOfChargePercent / 100m;
        var maximumChargeEnergyKwh = CalculateSlotEnergyKwh(batteryConfiguration.MaximumChargePowerWatts, priceSlot.TimeSlot);
        var maximumDischargeEnergyKwh = CalculateSlotEnergyKwh(batteryConfiguration.MaximumDischargePowerWatts, priceSlot.TimeSlot);

        if (gridImportBeforeBatteryKwh < 0m && batteryEnergyBeforeKwh < batteryConfiguration.TotalCapacityKwh)
        {
            var surplusEnergyKwh = Math.Abs(gridImportBeforeBatteryKwh);
            var chargeEnergyKwh = Math.Min(Math.Min(surplusEnergyKwh, maximumChargeEnergyKwh), batteryConfiguration.TotalCapacityKwh - batteryEnergyBeforeKwh);

            return CreateSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                gridImportBeforeBatteryKwh,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh + chargeEnergyKwh,
                batteryConfiguration,
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.PV),
                CalculatePowerWatts(chargeEnergyKwh, priceSlot.TimeSlot),
                "Baseline laedt nur PV-Ueberschuss in den Akku.");
        }

        if (gridImportBeforeBatteryKwh > 0m && batteryEnergyBeforeKwh > minimumBatteryEnergyKwh)
        {
            var availableDischargeEnergyKwh = batteryEnergyBeforeKwh - minimumBatteryEnergyKwh;
            var dischargeEnergyKwh = Math.Min(Math.Min(gridImportBeforeBatteryKwh, maximumDischargeEnergyKwh), availableDischargeEnergyKwh);

            return CreateSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                gridImportBeforeBatteryKwh,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh - dischargeEnergyKwh,
                batteryConfiguration,
                new BatteryDecisionInstruction(BatteryDecisionState.Discharge, chargeSource: null),
                CalculatePowerWatts(dischargeEnergyKwh, priceSlot.TimeSlot),
                "Baseline entlaedt nur zur Deckung des erwarteten Hausverbrauchs.");
        }

        return CreateSlot(
            priceSlot,
            pvSlot,
            consumptionSlot,
            gridImportBeforeBatteryKwh,
            stateOfChargeBeforePercent,
            batteryEnergyBeforeKwh,
            batteryConfiguration,
            new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
            targetPowerWatts: 0,
            "Baseline bleibt idle, weil weder nutzbarer PV-Ueberschuss noch deckbarer Netzbezug vorliegt.");
    }

    private static SimulatedBaselineSlot CreateSlot(
        TibberPriceForecastSlot priceSlot,
        PvYieldForecastSlot pvSlot,
        ConsumptionForecastSlot consumptionSlot,
        decimal gridImportBeforeBatteryKwh,
        decimal stateOfChargeBeforePercent,
        decimal batteryEnergyAfterKwh,
        BatteryConfiguration batteryConfiguration,
        BatteryDecisionInstruction instruction,
        int targetPowerWatts,
        string reason)
    {
        var boundedBatteryEnergyAfterKwh = Math.Clamp(batteryEnergyAfterKwh, 0m, batteryConfiguration.TotalCapacityKwh);
        var entry = new BatteryForecastEntry(
            priceSlot.TimeSlot,
            priceSlot.TotalPricePerKwh,
            priceSlot.Currency,
            pvSlot.ExpectedPvYieldKwh,
            consumptionSlot.ExpectedConsumptionKwh,
            gridImportBeforeBatteryKwh,
            stateOfChargeBeforePercent,
            CalculateStateOfChargePercent(boundedBatteryEnergyAfterKwh, batteryConfiguration),
            new CurrentBatteryDecision(instruction, targetPowerWatts),
            new[] { new BatteryDecisionReason(RuleId, reason) });

        return new SimulatedBaselineSlot(entry, boundedBatteryEnergyAfterKwh);
    }

    private static decimal CalculateSlotEnergyKwh(int powerWatts, ForecastTimeSlot timeSlot)
    {
        return powerWatts / 1000m * (decimal)timeSlot.Duration.TotalHours;
    }

    private static int CalculatePowerWatts(decimal energyKwh, ForecastTimeSlot timeSlot)
    {
        return (int)Math.Round(energyKwh / (decimal)timeSlot.Duration.TotalHours * 1000m, MidpointRounding.AwayFromZero);
    }

    private static decimal CalculateStateOfChargePercent(
        decimal batteryEnergyKwh,
        BatteryConfiguration batteryConfiguration)
    {
        return Math.Round(
            Math.Clamp(batteryEnergyKwh / batteryConfiguration.TotalCapacityKwh * 100m, 0m, 100m),
            4,
            MidpointRounding.AwayFromZero);
    }

    private sealed record SimulatedBaselineSlot(BatteryForecastEntry Entry, decimal BatteryEnergyAfterKwh);
}
