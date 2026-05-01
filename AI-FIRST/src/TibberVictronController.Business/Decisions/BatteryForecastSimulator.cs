using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Simulates Decision Engine forecast slots and projects the battery state of charge over time.
/// </summary>
public sealed class BatteryForecastSimulator
{
    private const decimal FullBatterySocPercent = 100m;
    private const decimal MinimumEconomicDischargePricePerKwh = 0.30m;

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
        var singleDirectionEfficiency = CalculateSingleDirectionEfficiency(batteryConfiguration);
        var maximumChargeInputEnergyKwh = CalculateSlotEnergyKwh(batteryConfiguration.MaximumChargePowerWatts, priceSlot.TimeSlot);
        var maximumDischargeOutputEnergyKwh = CalculateSlotEnergyKwh(batteryConfiguration.MaximumDischargePowerWatts, priceSlot.TimeSlot);

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
                maximumChargeInputEnergyKwh,
                singleDirectionEfficiency);
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
            maximumChargeInputEnergyKwh,
            maximumDischargeOutputEnergyKwh,
            singleDirectionEfficiency,
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
        decimal maximumChargeInputEnergyKwh,
        decimal singleDirectionEfficiency)
    {
        var availableCapacityKwh = batteryConfiguration.TotalCapacityKwh - batteryEnergyBeforeKwh;

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
                BatteryForecastRuleIds.BatteryFullPvSurplus,
                "Der erwartete Netzbezug ist negativ, aber der Akku ist voll und kann keinen Ueberschuss aufnehmen.");
        }

        if (ShouldPreserveHeadroomForFutureNegativePriceCharging(priceForecast, priceSlot.TimeSlot.StartsAtUtc, feedInCompensationPricePerKwh))
        {
            return CreateIdleSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                expectedGridImportBeforeBatteryKwh,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                BatteryForecastRuleIds.PreserveHeadroomForNegativePrice,
                "Die Decision Engine haelt Kapazitaet fuer spaetere negative Tibber-Preise frei, weil diese wertvoller sind als die konfigurierte Einspeiseverguetung.");
        }

        var surplusInputEnergyKwh = Math.Abs(expectedGridImportBeforeBatteryKwh);
        var chargeInputEnergyKwh = CalculateChargeInputEnergyKwh(surplusInputEnergyKwh, maximumChargeInputEnergyKwh, availableCapacityKwh, singleDirectionEfficiency);
        var storedEnergyKwh = chargeInputEnergyKwh * singleDirectionEfficiency;
        var targetPowerWatts = CalculatePowerWatts(chargeInputEnergyKwh, priceSlot.TimeSlot);

        return CreateSimulatedSlot(
            priceSlot,
            pvSlot,
            consumptionSlot,
            expectedGridImportBeforeBatteryKwh,
            stateOfChargeBeforePercent,
            batteryEnergyBeforeKwh + storedEnergyKwh,
            batteryConfiguration,
            new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.PV),
            targetPowerWatts,
            BatteryForecastRuleIds.PvSurplusCharge,
            $"Der erwartete Netzbezug ist negativ. Die Decision Engine nimmt {chargeInputEnergyKwh:0.0000} kWh PV-Ueberschuss auf und speichert nach Wirkungsgrad {storedEnergyKwh:0.0000} kWh im Akku.");
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
        decimal maximumChargeInputEnergyKwh,
        decimal maximumDischargeOutputEnergyKwh,
        decimal singleDirectionEfficiency,
        bool isPlannedGridChargeSlot)
    {
        var minimumBatteryEnergyKwh = CalculateMinimumBatteryEnergyKwh(batteryConfiguration);
        var targetEndBatteryEnergyKwh = CalculateTargetEndBatteryEnergyKwh(batteryConfiguration);
        var hasFutureNegativePriceWindow = HasFutureNegativePriceWindow(priceForecast, priceSlot.TimeSlot.StartsAtUtc);

        if (CanDischargeBeforeNegativePriceWindow(
            priceSlot,
            priceForecast,
            expectedGridImportBeforeBatteryKwh,
            batteryEnergyBeforeKwh,
            minimumBatteryEnergyKwh,
            maximumDischargeOutputEnergyKwh,
            singleDirectionEfficiency))
        {
            return CreateDischargeSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                expectedGridImportBeforeBatteryKwh,
                minimumBatteryEnergyKwh,
                maximumDischargeOutputEnergyKwh,
                singleDirectionEfficiency,
                BatteryForecastRuleIds.DischargeBeforeNegativePriceWindow,
                "Vor einem spaeteren Negativpreisfenster nutzt die Decision Engine vorhandene Akkuenergie, vermeidet normalen Netzbezug und schafft Kapazitaet fuer guenstiges Laden.");
        }

        if (priceSlot.TotalPricePerKwh < 0m)
        {
            if (batteryEnergyBeforeKwh >= batteryConfiguration.TotalCapacityKwh)
            {
                return CreateIdleSlot(
                    priceSlot,
                    pvSlot,
                    consumptionSlot,
                    expectedGridImportBeforeBatteryKwh,
                    stateOfChargeBeforePercent,
                    batteryEnergyBeforeKwh,
                    batteryConfiguration,
                    BatteryForecastRuleIds.BatteryFullIdle,
                    "Der Tibber-Preis ist negativ, aber der Akku ist voll und kann nicht weiter geladen werden.");
            }

            if (isPlannedGridChargeSlot || !HasFutureCheaperNegativePrice(priceForecast, priceSlot))
            {
                return CreateGridChargeSlot(
                    priceSlot,
                    pvSlot,
                    consumptionSlot,
                    stateOfChargeBeforePercent,
                    batteryEnergyBeforeKwh,
                    batteryConfiguration,
                    expectedGridImportBeforeBatteryKwh,
                    maximumChargeInputEnergyKwh,
                    singleDirectionEfficiency,
                    BatteryForecastRuleIds.NegativePriceGridCharge,
                    "Der Tibber-Preis ist negativ und gehoert zum guenstigen Ladefenster. Die Decision Engine laedt deshalb aus dem Netz.");
            }

            return CreateIdleSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                expectedGridImportBeforeBatteryKwh,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                BatteryForecastRuleIds.WaitForNegativePriceWindow,
                "Der Tibber-Preis ist negativ, aber ein noch guenstigerer geplanter Ladeslot bleibt frei. Die Decision Engine wartet auf das bessere Ladefenster.");
        }

        if (ShouldDischargeAtCurrentPrice(priceSlot, expectedGridImportBeforeBatteryKwh, batteryEnergyBeforeKwh, targetEndBatteryEnergyKwh))
        {
            return CreateDischargeSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                expectedGridImportBeforeBatteryKwh,
                targetEndBatteryEnergyKwh,
                maximumDischargeOutputEnergyKwh,
                singleDirectionEfficiency,
                BatteryForecastRuleIds.ExpensivePriceDischarge,
                "Der aktuelle Tibber-Preis ist hoch genug, um erwarteten Netzbezug aus dem Akku zu decken. Die konfigurierte Endreserve bleibt erhalten.");
        }

        if (expectedGridImportBeforeBatteryKwh > 0m &&
            priceSlot.TotalPricePerKwh >= MinimumEconomicDischargePricePerKwh &&
            hasFutureNegativePriceWindow &&
            batteryEnergyBeforeKwh <= minimumBatteryEnergyKwh)
        {
            return CreateIdleSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                expectedGridImportBeforeBatteryKwh,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                BatteryForecastRuleIds.MinimumSocReserve,
                "Akku wird nicht weiter entladen, weil der konfigurierte minimale Akkuladestand erreicht ist.");
        }

        if (expectedGridImportBeforeBatteryKwh > 0m &&
            priceSlot.TotalPricePerKwh >= MinimumEconomicDischargePricePerKwh &&
            !hasFutureNegativePriceWindow &&
            batteryEnergyBeforeKwh <= targetEndBatteryEnergyKwh)
        {
            return CreateIdleSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                expectedGridImportBeforeBatteryKwh,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                BatteryForecastRuleIds.EndSocReserve,
                "Akku wird wegen konfigurierter Endreserve nicht weiter entladen.");
        }

        var projectedBatteryState = new BatteryState(stateOfChargeBeforePercent, priceSlot.TimeSlot.StartsAtUtc);
        var priceRuleResult = tibberPriceDecisionRule.Evaluate(priceSlot.TimeSlot.StartsAtUtc, priceForecast, projectedBatteryState);

        if (isPlannedGridChargeSlot &&
            priceRuleResult.Instruction.DecisionState == BatteryDecisionState.Charge &&
            batteryEnergyBeforeKwh < batteryConfiguration.TotalCapacityKwh)
        {
            return CreateGridChargeSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                expectedGridImportBeforeBatteryKwh,
                maximumChargeInputEnergyKwh,
                singleDirectionEfficiency,
                BatteryForecastRuleIds.PlannedGridCharge,
                "Der Slot gehoert zu den guenstigsten geplanten Tibber-Ladeslots. Die Decision Engine laedt aus dem Netz.");
        }

        var idleRuleId = hasFutureNegativePriceWindow
            ? BatteryForecastRuleIds.WaitForNegativePriceWindow
            : BatteryForecastRuleIds.NeutralIdle;
        var idleReason = hasFutureNegativePriceWindow
            ? "Aktueller Preis ist nicht attraktiv genug, weil spaeter ein negatives Preisfenster erwartet wird."
            : "Der aktuelle Slot loest keine technisch oder wirtschaftlich bessere Batterieaktion aus. Die Decision Engine bleibt idle.";

        return CreateIdleSlot(
            priceSlot,
            pvSlot,
            consumptionSlot,
            expectedGridImportBeforeBatteryKwh,
            stateOfChargeBeforePercent,
            batteryEnergyBeforeKwh,
            batteryConfiguration,
            idleRuleId,
            idleReason);
    }

    private static SimulatedSlot CreateGridChargeSlot(
        TibberPriceForecastSlot priceSlot,
        PvYieldForecastSlot pvSlot,
        ConsumptionForecastSlot consumptionSlot,
        decimal stateOfChargeBeforePercent,
        decimal batteryEnergyBeforeKwh,
        BatteryConfiguration batteryConfiguration,
        decimal expectedGridImportBeforeBatteryKwh,
        decimal maximumChargeInputEnergyKwh,
        decimal singleDirectionEfficiency,
        string ruleId,
        string reasonMessage)
    {
        var availableCapacityKwh = batteryConfiguration.TotalCapacityKwh - batteryEnergyBeforeKwh;
        var chargeInputEnergyKwh = CalculateChargeInputEnergyKwh(maximumChargeInputEnergyKwh, maximumChargeInputEnergyKwh, availableCapacityKwh, singleDirectionEfficiency);
        var storedEnergyKwh = chargeInputEnergyKwh * singleDirectionEfficiency;
        var targetPowerWatts = CalculatePowerWatts(chargeInputEnergyKwh, priceSlot.TimeSlot);

        return CreateSimulatedSlot(
            priceSlot,
            pvSlot,
            consumptionSlot,
            expectedGridImportBeforeBatteryKwh,
            stateOfChargeBeforePercent,
            batteryEnergyBeforeKwh + storedEnergyKwh,
            batteryConfiguration,
            new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
            targetPowerWatts,
            ruleId,
            $"{reasonMessage} Es werden {chargeInputEnergyKwh:0.0000} kWh bezogen und nach Wirkungsgrad {storedEnergyKwh:0.0000} kWh gespeichert.");
    }

    private static SimulatedSlot CreateDischargeSlot(
        TibberPriceForecastSlot priceSlot,
        PvYieldForecastSlot pvSlot,
        ConsumptionForecastSlot consumptionSlot,
        decimal stateOfChargeBeforePercent,
        decimal batteryEnergyBeforeKwh,
        BatteryConfiguration batteryConfiguration,
        decimal expectedGridImportBeforeBatteryKwh,
        decimal reserveBatteryEnergyKwh,
        decimal maximumDischargeOutputEnergyKwh,
        decimal singleDirectionEfficiency,
        string ruleId,
        string reasonMessage)
    {
        var availableBatteryEnergyKwh = Math.Max(0m, batteryEnergyBeforeKwh - reserveBatteryEnergyKwh);
        var maximumLoadCoverageKwh = availableBatteryEnergyKwh * singleDirectionEfficiency;
        var dischargeOutputEnergyKwh = Math.Min(Math.Min(expectedGridImportBeforeBatteryKwh, maximumDischargeOutputEnergyKwh), maximumLoadCoverageKwh);
        var removedBatteryEnergyKwh = dischargeOutputEnergyKwh / singleDirectionEfficiency;
        var targetPowerWatts = CalculatePowerWatts(dischargeOutputEnergyKwh, priceSlot.TimeSlot);

        return CreateSimulatedSlot(
            priceSlot,
            pvSlot,
            consumptionSlot,
            expectedGridImportBeforeBatteryKwh,
            stateOfChargeBeforePercent,
            batteryEnergyBeforeKwh - removedBatteryEnergyKwh,
            batteryConfiguration,
            new BatteryDecisionInstruction(BatteryDecisionState.Discharge, chargeSource: null),
            targetPowerWatts,
            ruleId,
            $"{reasonMessage} Es werden {dischargeOutputEnergyKwh:0.0000} kWh Last gedeckt; dafuer sinkt der Akkuinhalt wegen Wirkungsgrad um {removedBatteryEnergyKwh:0.0000} kWh.");
    }

    private static bool CanDischargeBeforeNegativePriceWindow(
        TibberPriceForecastSlot priceSlot,
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        decimal expectedGridImportBeforeBatteryKwh,
        decimal batteryEnergyBeforeKwh,
        decimal minimumBatteryEnergyKwh,
        decimal maximumDischargeOutputEnergyKwh,
        decimal singleDirectionEfficiency)
    {
        if (priceSlot.TotalPricePerKwh < MinimumEconomicDischargePricePerKwh ||
            expectedGridImportBeforeBatteryKwh <= 0m ||
            batteryEnergyBeforeKwh <= minimumBatteryEnergyKwh)
        {
            return false;
        }

        var futureNegativeSlots = priceForecast
            .Where(futureSlot =>
                futureSlot.TimeSlot.StartsAtUtc > priceSlot.TimeSlot.StartsAtUtc &&
                futureSlot.TotalPricePerKwh < 0m)
            .ToArray();

        if (futureNegativeSlots.Length == 0)
        {
            return false;
        }

        var availableLoadCoverageKwh = (batteryEnergyBeforeKwh - minimumBatteryEnergyKwh) * singleDirectionEfficiency;

        return availableLoadCoverageKwh > 0m && maximumDischargeOutputEnergyKwh > 0m;
    }

    private static bool ShouldDischargeAtCurrentPrice(
        TibberPriceForecastSlot priceSlot,
        decimal expectedGridImportBeforeBatteryKwh,
        decimal batteryEnergyBeforeKwh,
        decimal reserveBatteryEnergyKwh)
    {
        return priceSlot.TotalPricePerKwh >= MinimumEconomicDischargePricePerKwh &&
            expectedGridImportBeforeBatteryKwh > 0m &&
            batteryEnergyBeforeKwh > reserveBatteryEnergyKwh;
    }

    private static SimulatedSlot CreateIdleSlot(
        TibberPriceForecastSlot priceSlot,
        PvYieldForecastSlot pvSlot,
        ConsumptionForecastSlot consumptionSlot,
        decimal expectedGridImportBeforeBatteryKwh,
        decimal stateOfChargeBeforePercent,
        decimal batteryEnergyKwh,
        BatteryConfiguration batteryConfiguration,
        string ruleId,
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
            ruleId,
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
        string ruleId,
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
            new[] { new BatteryDecisionReason(ruleId, reasonMessage) });

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
        DateTimeOffset currentSlotStartsAtUtc,
        decimal feedInCompensationPricePerKwh)
    {
        var mostValuableFutureNegativePrice = priceForecast
            .Where(priceSlot =>
                priceSlot.TimeSlot.StartsAtUtc > currentSlotStartsAtUtc &&
                priceSlot.TotalPricePerKwh < 0m)
            .Select(priceSlot => Math.Abs(priceSlot.TotalPricePerKwh))
            .DefaultIfEmpty(0m)
            .Max();

        return mostValuableFutureNegativePrice > feedInCompensationPricePerKwh;
    }

    private static bool HasFutureNegativePriceWindow(
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        DateTimeOffset currentSlotStartsAtUtc)
    {
        return priceForecast.Any(priceSlot =>
            priceSlot.TimeSlot.StartsAtUtc > currentSlotStartsAtUtc &&
            priceSlot.TotalPricePerKwh < 0m);
    }

    private static bool HasFutureCheaperNegativePrice(
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        TibberPriceForecastSlot currentPriceSlot)
    {
        return priceForecast.Any(priceSlot =>
            priceSlot.TimeSlot.StartsAtUtc > currentPriceSlot.TimeSlot.StartsAtUtc &&
            priceSlot.TotalPricePerKwh < currentPriceSlot.TotalPricePerKwh);
    }

    private static decimal CalculateChargeInputEnergyKwh(
        decimal availableInputEnergyKwh,
        decimal maximumChargeInputEnergyKwh,
        decimal availableStoredCapacityKwh,
        decimal singleDirectionEfficiency)
    {
        var capacityLimitedInputEnergyKwh = availableStoredCapacityKwh / singleDirectionEfficiency;

        return Math.Max(0m, Math.Min(Math.Min(availableInputEnergyKwh, maximumChargeInputEnergyKwh), capacityLimitedInputEnergyKwh));
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

    private static decimal CalculateTargetEndBatteryEnergyKwh(BatteryConfiguration batteryConfiguration)
    {
        return batteryConfiguration.TotalCapacityKwh * batteryConfiguration.TargetEndStateOfChargePercent / 100m;
    }

    private static decimal CalculateSingleDirectionEfficiency(BatteryConfiguration batteryConfiguration)
    {
        return (decimal)Math.Sqrt((double)(batteryConfiguration.RoundTripEfficiencyPercent / 100m));
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
