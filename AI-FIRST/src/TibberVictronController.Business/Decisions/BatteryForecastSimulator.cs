using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Simulates Decision Engine forecast slots and projects the battery state of charge over time.
/// </summary>
public sealed class BatteryForecastSimulator
{
    private const string RuleName = "ForecastSimulation";
    private const decimal FullBatterySocPercent = 100m;

    private readonly BatteryChargeWindowPlanner chargeWindowPlanner = new();
    private readonly TibberPriceDecisionRule tibberPriceDecisionRule = new();

    /// <summary>
    /// Calculates forecast entries by applying PV, Tibber price and battery capacity rules slot by slot.
    /// </summary>
    public BatteryForecastResult Simulate(
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        IReadOnlyList<PvYieldForecastSlot> pvForecast,
        IReadOnlyList<ConsumptionForecastSlot> consumptionForecast,
        BatteryState initialBatteryState,
        BatteryConfiguration batteryConfiguration,
        decimal feedInCompensationPricePerKwh)
    {
        ValidateInputs(priceForecast, pvForecast, consumptionForecast, initialBatteryState, batteryConfiguration, feedInCompensationPricePerKwh);

        var plannedGridChargeSlots = chargeWindowPlanner
            .PlanCheapestSlotsForFullCharge(priceForecast, initialBatteryState, batteryConfiguration)
            .PlannedChargeSlots
            .ToHashSet();
        var pvSlotsByTime = pvForecast.ToDictionary(pvSlot => pvSlot.TimeSlot);
        var consumptionSlotsByTime = consumptionForecast.ToDictionary(consumptionSlot => consumptionSlot.TimeSlot);
        var entries = new List<BatteryForecastEntry>();
        var currentBatteryEnergyKwh = batteryConfiguration.TotalCapacityKwh * initialBatteryState.StateOfChargePercent / 100m;

        foreach (var priceSlot in priceForecast.OrderBy(priceSlot => priceSlot.TimeSlot.StartsAtUtc))
        {
            var pvSlot = pvSlotsByTime[priceSlot.TimeSlot];
            var consumptionSlot = consumptionSlotsByTime[priceSlot.TimeSlot];
            var stateOfChargeBeforePercent = CalculateStateOfChargePercent(currentBatteryEnergyKwh, batteryConfiguration);
            var simulatedSlot = SimulateSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                stateOfChargeBeforePercent,
                currentBatteryEnergyKwh,
                batteryConfiguration,
                plannedGridChargeSlots.Contains(priceSlot.TimeSlot),
                priceForecast,
                feedInCompensationPricePerKwh);

            currentBatteryEnergyKwh = simulatedSlot.BatteryEnergyAfterKwh;
            entries.Add(simulatedSlot.Entry);
        }

        return new BatteryForecastResult(initialBatteryState, batteryConfiguration, entries);
    }

    private SimulatedSlot SimulateSlot(
        TibberPriceForecastSlot priceSlot,
        PvYieldForecastSlot pvSlot,
        ConsumptionForecastSlot consumptionSlot,
        decimal stateOfChargeBeforePercent,
        decimal batteryEnergyBeforeKwh,
        BatteryConfiguration batteryConfiguration,
        bool isPlannedGridChargeSlot,
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        decimal feedInCompensationPricePerKwh)
    {
        var expectedGridImportBeforeBatteryKwh = consumptionSlot.ExpectedConsumptionKwh - pvSlot.ExpectedPvYieldKwh;
        var availableCapacityKwh = batteryConfiguration.TotalCapacityKwh - batteryEnergyBeforeKwh;
        var availableDischargeEnergyKwh = Math.Max(0m, batteryEnergyBeforeKwh - CalculateMinimumBatteryEnergyKwh(batteryConfiguration));
        var maximumChargeEnergyKwh = CalculateSlotEnergyKwh(batteryConfiguration.MaximumChargePowerWatts, priceSlot.TimeSlot);
        var maximumDischargeEnergyKwh = CalculateSlotEnergyKwh(batteryConfiguration.MaximumDischargePowerWatts, priceSlot.TimeSlot);

        if (expectedGridImportBeforeBatteryKwh < 0m)
        {
            return SimulatePvSurplusSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                priceForecast,
                feedInCompensationPricePerKwh,
                expectedGridImportBeforeBatteryKwh,
                availableCapacityKwh,
                maximumChargeEnergyKwh);
        }

        return SimulateTibberPriceSlot(
            priceSlot,
            pvSlot,
            consumptionSlot,
            stateOfChargeBeforePercent,
            batteryEnergyBeforeKwh,
            batteryConfiguration,
            priceForecast,
            expectedGridImportBeforeBatteryKwh,
            availableDischargeEnergyKwh,
            maximumDischargeEnergyKwh,
            isPlannedGridChargeSlot);
    }

    private SimulatedSlot SimulatePvSurplusSlot(
        TibberPriceForecastSlot priceSlot,
        PvYieldForecastSlot pvSlot,
        ConsumptionForecastSlot consumptionSlot,
        decimal stateOfChargeBeforePercent,
        decimal batteryEnergyBeforeKwh,
        BatteryConfiguration batteryConfiguration,
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        decimal feedInCompensationPricePerKwh,
        decimal expectedGridImportBeforeBatteryKwh,
        decimal availableCapacityKwh,
        decimal maximumChargeEnergyKwh)
    {
        if (availableCapacityKwh <= 0m)
        {
            return CreateIdleSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                expectedGridImportBeforeBatteryKwh,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                "Der erwartete Netzbezug ist negativ, aber der Akku ist voll und kann keinen Ueberschuss aufnehmen.");
        }

        if (ShouldPreserveHeadroomForFutureNegativePriceCharging(priceForecast, feedInCompensationPricePerKwh))
        {
            return CreateIdleSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                expectedGridImportBeforeBatteryKwh,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                "Die Decision Engine haelt Kapazitaet fuer spaetere negative Tibber-Preise frei, weil diese wertvoller sind als die konfigurierte Einspeiseverguetung.");
        }

        var surplusEnergyKwh = Math.Abs(expectedGridImportBeforeBatteryKwh);
        var chargeEnergyKwh = Math.Min(Math.Min(surplusEnergyKwh, maximumChargeEnergyKwh), availableCapacityKwh);
        var targetPowerWatts = CalculatePowerWatts(chargeEnergyKwh, priceSlot.TimeSlot);

        return CreateSimulatedSlot(
            priceSlot,
            pvSlot,
            consumptionSlot,
            expectedGridImportBeforeBatteryKwh,
            stateOfChargeBeforePercent,
            batteryEnergyBeforeKwh + chargeEnergyKwh,
            batteryConfiguration,
            new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.PV),
            targetPowerWatts,
            $"Der erwartete Netzbezug ist negativ. Die Decision Engine nimmt {chargeEnergyKwh:0.0000} kWh Ueberschuss in den Akku auf.");
    }

    private SimulatedSlot SimulateTibberPriceSlot(
        TibberPriceForecastSlot priceSlot,
        PvYieldForecastSlot pvSlot,
        ConsumptionForecastSlot consumptionSlot,
        decimal stateOfChargeBeforePercent,
        decimal batteryEnergyBeforeKwh,
        BatteryConfiguration batteryConfiguration,
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        decimal expectedGridImportBeforeBatteryKwh,
        decimal availableDischargeEnergyKwh,
        decimal maximumDischargeEnergyKwh,
        bool isPlannedGridChargeSlot)
    {
        var projectedBatteryState = new BatteryState(stateOfChargeBeforePercent, priceSlot.TimeSlot.StartsAtUtc);
        var priceRuleResult = tibberPriceDecisionRule.Evaluate(priceSlot.TimeSlot.StartsAtUtc, priceForecast, projectedBatteryState);

        if (priceRuleResult.Instruction.DecisionState == BatteryDecisionState.Discharge &&
            expectedGridImportBeforeBatteryKwh > 0m &&
            availableDischargeEnergyKwh > 0m)
        {
            var dischargeEnergyKwh = Math.Min(Math.Min(expectedGridImportBeforeBatteryKwh, maximumDischargeEnergyKwh), availableDischargeEnergyKwh);
            var targetPowerWatts = CalculatePowerWatts(dischargeEnergyKwh, priceSlot.TimeSlot);

            return CreateSimulatedSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                expectedGridImportBeforeBatteryKwh,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh - dischargeEnergyKwh,
                batteryConfiguration,
                priceRuleResult.Instruction,
                targetPowerWatts,
                $"Der Tibber-Preis ist teuer und es werden {dischargeEnergyKwh:0.0000} kWh erwarteter Netzbezug gedeckt, ohne ins Netz einzuspeisen.");
        }

        if (isPlannedGridChargeSlot &&
            priceRuleResult.Instruction.DecisionState == BatteryDecisionState.Charge &&
            batteryEnergyBeforeKwh < batteryConfiguration.TotalCapacityKwh)
        {
            var chargeEnergyKwh = Math.Min(CalculateSlotEnergyKwh(batteryConfiguration.MaximumChargePowerWatts, priceSlot.TimeSlot), batteryConfiguration.TotalCapacityKwh - batteryEnergyBeforeKwh);
            var targetPowerWatts = CalculatePowerWatts(chargeEnergyKwh, priceSlot.TimeSlot);

            return CreateSimulatedSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                expectedGridImportBeforeBatteryKwh,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh + chargeEnergyKwh,
                batteryConfiguration,
                priceRuleResult.Instruction,
                targetPowerWatts,
                $"Der Slot gehoert zu den guenstigsten geplanten Tibber-Ladeslots. Die Decision Engine laedt {chargeEnergyKwh:0.0000} kWh aus dem Netz.");
        }

        return CreateIdleSlot(
            priceSlot,
            pvSlot,
            consumptionSlot,
            expectedGridImportBeforeBatteryKwh,
            stateOfChargeBeforePercent,
            batteryEnergyBeforeKwh,
            batteryConfiguration,
            priceRuleResult.Reasons[0].Message);
    }

    private static SimulatedSlot CreateIdleSlot(
        TibberPriceForecastSlot priceSlot,
        PvYieldForecastSlot pvSlot,
        ConsumptionForecastSlot consumptionSlot,
        decimal expectedGridImportBeforeBatteryKwh,
        decimal stateOfChargeBeforePercent,
        decimal batteryEnergyKwh,
        BatteryConfiguration batteryConfiguration,
        string reasonMessage)
    {
        return CreateSimulatedSlot(
            priceSlot,
            pvSlot,
            consumptionSlot,
            expectedGridImportBeforeBatteryKwh,
            stateOfChargeBeforePercent,
            batteryEnergyKwh,
            batteryConfiguration,
            new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null),
            targetPowerWatts: 0,
            reasonMessage);
    }

    private static SimulatedSlot CreateSimulatedSlot(
        TibberPriceForecastSlot priceSlot,
        PvYieldForecastSlot pvSlot,
        ConsumptionForecastSlot consumptionSlot,
        decimal expectedGridImportBeforeBatteryKwh,
        decimal stateOfChargeBeforePercent,
        decimal batteryEnergyAfterKwh,
        BatteryConfiguration batteryConfiguration,
        BatteryDecisionInstruction instruction,
        int targetPowerWatts,
        string reasonMessage)
    {
        var boundedBatteryEnergyAfterKwh = Math.Clamp(batteryEnergyAfterKwh, 0m, batteryConfiguration.TotalCapacityKwh);
        var stateOfChargeAfterPercent = CalculateStateOfChargePercent(boundedBatteryEnergyAfterKwh, batteryConfiguration);
        var decision = new CurrentBatteryDecision(instruction, targetPowerWatts);
        var entry = new BatteryForecastEntry(
            priceSlot.TimeSlot,
            priceSlot.TotalPricePerKwh,
            priceSlot.Currency,
            pvSlot.ExpectedPvYieldKwh,
            consumptionSlot.ExpectedConsumptionKwh,
            expectedGridImportBeforeBatteryKwh,
            stateOfChargeBeforePercent,
            stateOfChargeAfterPercent,
            decision,
            new[] { new BatteryDecisionReason(RuleName, reasonMessage) });

        return new SimulatedSlot(entry, boundedBatteryEnergyAfterKwh);
    }

    private static void ValidateInputs(
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        IReadOnlyList<PvYieldForecastSlot> pvForecast,
        IReadOnlyList<ConsumptionForecastSlot> consumptionForecast,
        BatteryState initialBatteryState,
        BatteryConfiguration batteryConfiguration,
        decimal feedInCompensationPricePerKwh)
    {
        if (priceForecast is null)
        {
            throw new ArgumentNullException(nameof(priceForecast), "Die Tibber-Preisprognose darf nicht null sein.");
        }

        if (pvForecast is null)
        {
            throw new ArgumentNullException(nameof(pvForecast), "Die PV-Prognose darf nicht null sein.");
        }

        if (consumptionForecast is null)
        {
            throw new ArgumentNullException(nameof(consumptionForecast), "Die Verbrauchsprognose darf nicht null sein.");
        }

        if (initialBatteryState is null)
        {
            throw new ArgumentNullException(nameof(initialBatteryState), "Der initiale Akkuladestand darf nicht null sein.");
        }

        if (batteryConfiguration is null)
        {
            throw new ArgumentNullException(nameof(batteryConfiguration), "Die Batteriekonfiguration darf nicht null sein.");
        }

        if (feedInCompensationPricePerKwh < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(feedInCompensationPricePerKwh), "Die Einspeiseverguetung darf nicht negativ sein.");
        }

        ValidateMatchingTimeSlots(priceForecast, pvForecast, consumptionForecast);
    }

    private static void ValidateMatchingTimeSlots(
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        IReadOnlyList<PvYieldForecastSlot> pvForecast,
        IReadOnlyList<ConsumptionForecastSlot> consumptionForecast)
    {
        var priceTimeSlots = priceForecast.Select(priceSlot => priceSlot.TimeSlot).ToArray();
        var pvTimeSlots = pvForecast.Select(pvSlot => pvSlot.TimeSlot).ToArray();
        var consumptionTimeSlots = consumptionForecast.Select(consumptionSlot => consumptionSlot.TimeSlot).ToArray();

        if (!priceTimeSlots.SequenceEqual(pvTimeSlots) || !priceTimeSlots.SequenceEqual(consumptionTimeSlots))
        {
            throw new ArgumentException("Tibber-Preise, PV-Prognose und Verbrauchsprognose muessen dieselben Forecast-Zeitabschnitte enthalten.");
        }
    }

    private static bool ShouldPreserveHeadroomForFutureNegativePriceCharging(
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        decimal feedInCompensationPricePerKwh)
    {
        var mostValuableNegativePrice = priceForecast
            .Where(priceSlot => priceSlot.TotalPricePerKwh < 0m)
            .Select(priceSlot => Math.Abs(priceSlot.TotalPricePerKwh))
            .DefaultIfEmpty(0m)
            .Max();

        return mostValuableNegativePrice > feedInCompensationPricePerKwh;
    }

    private static decimal CalculateSlotEnergyKwh(int powerWatts, ForecastTimeSlot timeSlot)
    {
        return powerWatts / 1000m * (decimal)timeSlot.Duration.TotalHours;
    }

    private static int CalculatePowerWatts(decimal energyKwh, ForecastTimeSlot timeSlot)
    {
        return (int)Math.Round(energyKwh / (decimal)timeSlot.Duration.TotalHours * 1000m, MidpointRounding.AwayFromZero);
    }

    private static decimal CalculateMinimumBatteryEnergyKwh(BatteryConfiguration batteryConfiguration)
    {
        return batteryConfiguration.TotalCapacityKwh * batteryConfiguration.MinimumStateOfChargePercent / 100m;
    }

    private static decimal CalculateStateOfChargePercent(
        decimal batteryEnergyKwh,
        BatteryConfiguration batteryConfiguration)
    {
        var stateOfChargePercent = batteryEnergyKwh / batteryConfiguration.TotalCapacityKwh * 100m;

        return Math.Round(Math.Clamp(stateOfChargePercent, 0m, FullBatterySocPercent), 4, MidpointRounding.AwayFromZero);
    }

    private sealed record SimulatedSlot(BatteryForecastEntry Entry, decimal BatteryEnergyAfterKwh);
}
