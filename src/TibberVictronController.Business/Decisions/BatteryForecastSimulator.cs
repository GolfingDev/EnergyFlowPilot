using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Simulates Decision Engine forecast slots and projects the battery state of charge over time.
/// </summary>
public sealed class BatteryForecastSimulator
{
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
        var averagePricePerKwh = priceForecast.Average(priceSlot => priceSlot.TotalPricePerKwh);

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
                pvForecast,
                consumptionForecast,
                averagePricePerKwh,
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
        IReadOnlyList<PvYieldForecastSlot> pvForecast,
        IReadOnlyList<ConsumptionForecastSlot> consumptionForecast,
        decimal averagePricePerKwh,
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
            pvForecast,
            consumptionForecast,
            expectedGridImportBeforeBatteryKwh,
            maximumChargeInputEnergyKwh,
            maximumDischargeOutputEnergyKwh,
            singleDirectionEfficiency,
            isPlannedGridChargeSlot,
            averagePricePerKwh,
            feedInCompensationPricePerKwh);
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
        var maximumPlannedBatteryEnergyKwh = CalculatePlanningMaximumBatteryEnergyKwh(batteryConfiguration);
        var availableCapacityKwh = maximumPlannedBatteryEnergyKwh - batteryEnergyBeforeKwh;

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
                BatteryForecastRuleIds.PlanningMaximumSocHeadroom,
                $"Der erwartete Netzbezug ist negativ, aber das Planungs-Maximum von {batteryConfiguration.PlanningMaximumStateOfChargePercent:0.#} Prozent ist erreicht. Die Decision Engine nimmt keinen weiteren Überschuss auf.");
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
        IReadOnlyList<PvYieldForecastSlot> pvForecast,
        IReadOnlyList<ConsumptionForecastSlot> consumptionForecast,
        decimal expectedGridImportBeforeBatteryKwh,
        decimal maximumChargeInputEnergyKwh,
        decimal maximumDischargeOutputEnergyKwh,
        decimal singleDirectionEfficiency,
        bool isPlannedGridChargeSlot,
        decimal averagePricePerKwh,
        decimal feedInCompensationPricePerKwh)
    {
        var planningMinimumBatteryEnergyKwh = CalculatePlanningMinimumBatteryEnergyKwh(batteryConfiguration);
        var targetEndBatteryEnergyKwh = CalculateTargetEndBatteryEnergyKwh(batteryConfiguration);
        var hasFutureNegativePriceWindow = HasFutureNegativePriceWindow(priceForecast, priceSlot.TimeSlot.StartsAtUtc);

        if (CanDischargeBeforeNegativePriceWindow(
            priceSlot,
            priceForecast,
            expectedGridImportBeforeBatteryKwh,
            batteryEnergyBeforeKwh,
            planningMinimumBatteryEnergyKwh,
            maximumDischargeOutputEnergyKwh,
            singleDirectionEfficiency,
            averagePricePerKwh))
        {
            return CreateDischargeSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                expectedGridImportBeforeBatteryKwh,
                planningMinimumBatteryEnergyKwh,
                maximumDischargeOutputEnergyKwh,
                singleDirectionEfficiency,
                BatteryForecastRuleIds.DischargeBeforeNegativePriceWindow,
                "Vor einem spaeteren Negativpreisfenster nutzt die Decision Engine vorhandene Akkuenergie, vermeidet normalen Netzbezug und schafft Kapazitaet fuer guenstiges Laden.");
        }

        if (priceSlot.TotalPricePerKwh < 0m)
        {
            if (batteryEnergyBeforeKwh >= CalculatePlanningMaximumBatteryEnergyKwh(batteryConfiguration))
            {
                return CreateIdleSlot(
                    priceSlot,
                    pvSlot,
                    consumptionSlot,
                    expectedGridImportBeforeBatteryKwh,
                    stateOfChargeBeforePercent,
                    batteryEnergyBeforeKwh,
                    batteryConfiguration,
                    BatteryForecastRuleIds.PlanningMaximumSocHeadroom,
                    $"Der Tibber-Preis ist negativ, aber das Planungs-Maximum von {batteryConfiguration.PlanningMaximumStateOfChargePercent:0.#} Prozent ist erreicht. Die Decision Engine lädt nicht weiter.");
            }

            if (isPlannedGridChargeSlot || !HasFutureCheaperNegativePrice(priceForecast, priceSlot))
            {
                return CreateGridChargeSlot(new GridChargeSlotInput
                {
                    PriceSlot = priceSlot,
                    PvSlot = pvSlot,
                    ConsumptionSlot = consumptionSlot,
                    StateOfChargeBeforePercent = stateOfChargeBeforePercent,
                    BatteryEnergyBeforeKwh = batteryEnergyBeforeKwh,
                    BatteryConfiguration = batteryConfiguration,
                    ExpectedGridImportBeforeBatteryKwh = expectedGridImportBeforeBatteryKwh,
                    MaximumChargeInputEnergyKwh = maximumChargeInputEnergyKwh,
                    SingleDirectionEfficiency = singleDirectionEfficiency,
                    FeedInCompensationPricePerKwh = feedInCompensationPricePerKwh,
                    RuleId = BatteryForecastRuleIds.NegativePriceGridCharge,
                    ReasonMessage = "Der Tibber-Preis ist negativ und gehoert zum guenstigen Ladefenster. Die Decision Engine laedt deshalb aus dem Netz."
                });
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

        if (ShouldDischargeForFuturePvHeadroom(
            priceSlot,
            pvForecast,
            consumptionForecast,
            expectedGridImportBeforeBatteryKwh,
            batteryEnergyBeforeKwh,
            batteryConfiguration,
            planningMinimumBatteryEnergyKwh,
            maximumDischargeOutputEnergyKwh,
            singleDirectionEfficiency,
            feedInCompensationPricePerKwh))
        {
            var futurePvSurplusInputKwh = CalculateFuturePvSurplusInputKwh(pvForecast, consumptionForecast, priceSlot.TimeSlot.StartsAtUtc);
            var currentHeadroomInputKwh = CalculateAvailablePvInputHeadroomKwh(batteryConfiguration, batteryEnergyBeforeKwh, singleDirectionEfficiency);

            return CreateDischargeSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                expectedGridImportBeforeBatteryKwh,
                planningMinimumBatteryEnergyKwh,
                maximumDischargeOutputEnergyKwh,
                singleDirectionEfficiency,
                BatteryForecastRuleIds.DischargeForFuturePvHeadroom,
                $"Es werden spaeter {futurePvSurplusInputKwh:0.0000} kWh PV-Ueberschuss erwartet, aber aktuell sind nur {currentHeadroomInputKwh:0.0000} kWh Ladepuffer frei. Weil der aktuelle Preis mit {priceSlot.TotalPricePerKwh:0.0000} EUR/kWh nicht unter der Einspeiseverguetung von {feedInCompensationPricePerKwh:0.0000} EUR/kWh liegt, deckt die Decision Engine jetzigen Netzbezug aus dem Akku und schafft PV-Puffer.");
        }

        if (ShouldDischargeAtCurrentPrice(priceSlot, expectedGridImportBeforeBatteryKwh, batteryEnergyBeforeKwh, targetEndBatteryEnergyKwh, averagePricePerKwh))
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
                $"Der aktuelle Tibber-Preis liegt mit {priceSlot.TotalPricePerKwh:0.0000} EUR/kWh mindestens auf dem Forecast-Durchschnitt von {averagePricePerKwh:0.0000} EUR/kWh. Die Decision Engine deckt erwarteten Netzbezug aus dem Akku; die konfigurierte Endreserve bleibt erhalten.");
        }

        if (expectedGridImportBeforeBatteryKwh > 0m &&
            priceSlot.TotalPricePerKwh >= averagePricePerKwh &&
            hasFutureNegativePriceWindow &&
            batteryEnergyBeforeKwh <= planningMinimumBatteryEnergyKwh)
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
                "Akku wird nicht weiter entladen, weil die konfigurierte Planungsreserve erreicht ist.");
        }

        if (expectedGridImportBeforeBatteryKwh > 0m &&
            priceSlot.TotalPricePerKwh >= averagePricePerKwh &&
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
            batteryEnergyBeforeKwh < CalculatePlanningMaximumBatteryEnergyKwh(batteryConfiguration))
        {
            return CreateGridChargeSlot(new GridChargeSlotInput
            {
                PriceSlot = priceSlot,
                PvSlot = pvSlot,
                ConsumptionSlot = consumptionSlot,
                StateOfChargeBeforePercent = stateOfChargeBeforePercent,
                BatteryEnergyBeforeKwh = batteryEnergyBeforeKwh,
                BatteryConfiguration = batteryConfiguration,
                ExpectedGridImportBeforeBatteryKwh = expectedGridImportBeforeBatteryKwh,
                MaximumChargeInputEnergyKwh = maximumChargeInputEnergyKwh,
                SingleDirectionEfficiency = singleDirectionEfficiency,
                FeedInCompensationPricePerKwh = feedInCompensationPricePerKwh,
                RuleId = BatteryForecastRuleIds.PlannedGridCharge,
                ReasonMessage = "Der Slot gehoert zu den guenstigsten geplanten Tibber-Ladeslots. Die Decision Engine laedt aus dem Netz."
            });
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

    private static SimulatedSlot CreateGridChargeSlot(GridChargeSlotInput input)
    {
        var availableCapacityKwh = CalculateAvailableGridChargeStoredCapacityKwh(
            input.BatteryConfiguration,
            input.BatteryEnergyBeforeKwh);
        var physicalCapacityKwh = input.BatteryConfiguration.TotalCapacityKwh - input.BatteryEnergyBeforeKwh;
        var chargeInputEnergyKwh = CalculateChargeInputEnergyKwh(input.MaximumChargeInputEnergyKwh, input.MaximumChargeInputEnergyKwh, availableCapacityKwh, input.SingleDirectionEfficiency);
        var unrestrictedChargeInputEnergyKwh = CalculateChargeInputEnergyKwh(input.MaximumChargeInputEnergyKwh, input.MaximumChargeInputEnergyKwh, physicalCapacityKwh, input.SingleDirectionEfficiency);

        if (chargeInputEnergyKwh <= 0m)
        {
            return CreateIdleSlot(
                input.PriceSlot,
                input.PvSlot,
                input.ConsumptionSlot,
                input.ExpectedGridImportBeforeBatteryKwh,
                input.StateOfChargeBeforePercent,
                input.BatteryEnergyBeforeKwh,
                input.BatteryConfiguration,
                BatteryForecastRuleIds.PlanningMaximumSocHeadroom,
                "Die Decision Engine laedt nicht aus dem Netz, weil das Planungs-Maximum erreicht ist und Kapazitaet fuer moegliche PV-Prognoseabweichungen frei bleiben soll.");
        }

        var storedEnergyKwh = chargeInputEnergyKwh * input.SingleDirectionEfficiency;
        var targetPowerWatts = CalculatePowerWatts(chargeInputEnergyKwh, input.PriceSlot.TimeSlot);
        var isLimitedByPlanningMaximum = chargeInputEnergyKwh < unrestrictedChargeInputEnergyKwh;
        var planningMaximumReason = isLimitedByPlanningMaximum
            ? " Das Planungs-Maximum lässt einen PV-Prognosepuffer frei."
            : string.Empty;
        var ruleId = isLimitedByPlanningMaximum
            ? BatteryForecastRuleIds.PlanningMaximumGridChargeLimit
            : input.RuleId;

        return CreateSimulatedSlot(
            input.PriceSlot,
            input.PvSlot,
            input.ConsumptionSlot,
            input.ExpectedGridImportBeforeBatteryKwh,
            input.StateOfChargeBeforePercent,
            input.BatteryEnergyBeforeKwh + storedEnergyKwh,
            input.BatteryConfiguration,
            new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
            targetPowerWatts,
            ruleId,
            $"{input.ReasonMessage}{planningMaximumReason} Es werden {chargeInputEnergyKwh:0.0000} kWh bezogen und nach Wirkungsgrad {storedEnergyKwh:0.0000} kWh gespeichert.");
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
        decimal singleDirectionEfficiency,
        decimal averagePricePerKwh)
    {
        if (priceSlot.TotalPricePerKwh < averagePricePerKwh ||
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
        decimal reserveBatteryEnergyKwh,
        decimal averagePricePerKwh)
    {
        return priceSlot.TotalPricePerKwh >= averagePricePerKwh &&
            expectedGridImportBeforeBatteryKwh > 0m &&
            batteryEnergyBeforeKwh > reserveBatteryEnergyKwh;
    }

    private static bool ShouldDischargeForFuturePvHeadroom(
        TibberPriceForecastSlot priceSlot,
        IReadOnlyList<PvYieldForecastSlot> pvForecast,
        IReadOnlyList<ConsumptionForecastSlot> consumptionForecast,
        decimal expectedGridImportBeforeBatteryKwh,
        decimal batteryEnergyBeforeKwh,
        BatteryConfiguration batteryConfiguration,
        decimal planningMinimumBatteryEnergyKwh,
        decimal maximumDischargeOutputEnergyKwh,
        decimal singleDirectionEfficiency,
        decimal feedInCompensationPricePerKwh)
    {
        if (priceSlot.TotalPricePerKwh < feedInCompensationPricePerKwh ||
            expectedGridImportBeforeBatteryKwh <= 0m ||
            batteryEnergyBeforeKwh <= planningMinimumBatteryEnergyKwh ||
            maximumDischargeOutputEnergyKwh <= 0m)
        {
            return false;
        }

        var futurePvSurplusInputKwh = CalculateFuturePvSurplusInputKwh(pvForecast, consumptionForecast, priceSlot.TimeSlot.StartsAtUtc);
        var currentHeadroomInputKwh = CalculateAvailablePvInputHeadroomKwh(batteryConfiguration, batteryEnergyBeforeKwh, singleDirectionEfficiency);

        return futurePvSurplusInputKwh > currentHeadroomInputKwh;
    }

    private static decimal CalculateFuturePvSurplusInputKwh(
        IReadOnlyList<PvYieldForecastSlot> pvForecast,
        IReadOnlyList<ConsumptionForecastSlot> consumptionForecast,
        DateTimeOffset currentSlotStartsAtUtc)
    {
        var consumptionByTimeSlot = consumptionForecast.ToDictionary(consumptionSlot => consumptionSlot.TimeSlot);

        return pvForecast
            .Where(pvSlot => pvSlot.TimeSlot.StartsAtUtc > currentSlotStartsAtUtc)
            .Sum(pvSlot =>
            {
                var consumptionSlot = consumptionByTimeSlot[pvSlot.TimeSlot];

                return Math.Max(0m, pvSlot.ExpectedPvYieldKwh - consumptionSlot.ExpectedConsumptionKwh);
            });
    }

    private static decimal CalculateAvailablePvInputHeadroomKwh(
        BatteryConfiguration batteryConfiguration,
        decimal batteryEnergyBeforeKwh,
        decimal singleDirectionEfficiency)
    {
        var availableStoredCapacityKwh = CalculatePlanningMaximumBatteryEnergyKwh(batteryConfiguration) - batteryEnergyBeforeKwh;

        return Math.Max(0m, availableStoredCapacityKwh / singleDirectionEfficiency);
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
        var boundedBatteryEnergyAfterKwh = Math.Clamp(
            batteryEnergyAfterKwh,
            0m,
            CalculatePlanningMaximumBatteryEnergyKwh(batteryConfiguration));
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

    private static decimal CalculateAvailableGridChargeStoredCapacityKwh(
        BatteryConfiguration batteryConfiguration,
        decimal batteryEnergyBeforeKwh)
    {
        var planningMaximumBatteryEnergyKwh = CalculatePlanningMaximumBatteryEnergyKwh(batteryConfiguration);

        return planningMaximumBatteryEnergyKwh - batteryEnergyBeforeKwh;
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

    private static decimal CalculatePlanningMinimumBatteryEnergyKwh(BatteryConfiguration batteryConfiguration)
    {
        return batteryConfiguration.TotalCapacityKwh * batteryConfiguration.PlanningMinimumStateOfChargePercent / 100m;
    }

    private static decimal CalculateTargetEndBatteryEnergyKwh(BatteryConfiguration batteryConfiguration)
    {
        return batteryConfiguration.TotalCapacityKwh * batteryConfiguration.TargetEndStateOfChargePercent / 100m;
    }

    private static decimal CalculatePlanningMaximumBatteryEnergyKwh(BatteryConfiguration batteryConfiguration)
    {
        return batteryConfiguration.TotalCapacityKwh * batteryConfiguration.PlanningMaximumStateOfChargePercent / 100m;
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

        return Math.Round(Math.Clamp(stateOfChargePercent, 0m, batteryConfiguration.PlanningMaximumStateOfChargePercent), 4, MidpointRounding.AwayFromZero);
    }

    private sealed record SimulatedSlot(BatteryForecastEntry Entry, decimal BatteryEnergyAfterKwh);

    private sealed class GridChargeSlotInput
    {
        public required TibberPriceForecastSlot PriceSlot { get; init; }

        public required PvYieldForecastSlot PvSlot { get; init; }

        public required ConsumptionForecastSlot ConsumptionSlot { get; init; }

        public decimal StateOfChargeBeforePercent { get; init; }

        public decimal BatteryEnergyBeforeKwh { get; init; }

        public required BatteryConfiguration BatteryConfiguration { get; init; }

        public decimal ExpectedGridImportBeforeBatteryKwh { get; init; }

        public decimal MaximumChargeInputEnergyKwh { get; init; }

        public decimal SingleDirectionEfficiency { get; init; }

        public decimal FeedInCompensationPricePerKwh { get; init; }

        public required string RuleId { get; init; }

        public required string ReasonMessage { get; init; }
    }
}
