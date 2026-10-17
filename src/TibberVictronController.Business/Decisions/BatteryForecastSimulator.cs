using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Simulates Decision Engine forecast slots and projects the battery state of charge over time.
/// </summary>
public sealed class BatteryForecastSimulator
{
    private const decimal EnergyToleranceKwh = 0.001m;

    private readonly BatteryChargeWindowPlanner chargeWindowPlanner = new();
    private readonly BatterySocOptimizer socOptimizer = new();

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

        // Negative prices pay for every stored kWh, so the cheapest negative slots are still planned for a full charge.
        var plannedNegativePriceChargeSlots = chargeWindowPlanner
            .PlanCheapestSlotsForFullCharge(
                priceForecast.Where(priceSlot => priceSlot.TotalPricePerKwh < 0m).ToArray(),
                initialBatteryState,
                batteryConfiguration)
            .PlannedChargeSlots
            .ToHashSet();
        var pvSlotsByTime = pvForecast.ToDictionary(pvSlot => pvSlot.TimeSlot);
        var consumptionSlotsByTime = consumptionForecast.ToDictionary(consumptionSlot => consumptionSlot.TimeSlot);
        var needPlan = socOptimizer.Plan(new BatterySocOptimizationInput
        {
            Slots = priceForecast
                .OrderBy(priceSlot => priceSlot.TimeSlot.StartsAtUtc)
                .Select(priceSlot => new BatterySocOptimizationSlot(
                    priceSlot.TimeSlot,
                    priceSlot.TotalPricePerKwh,
                    priceSlot.IsEstimated,
                    consumptionSlotsByTime[priceSlot.TimeSlot].ExpectedConsumptionKwh - pvSlotsByTime[priceSlot.TimeSlot].ExpectedPvYieldKwh))
                .ToArray(),
            InitialEnergyKwh = batteryConfiguration.TotalCapacityKwh * initialBatteryState.StateOfChargePercent / 100m,
            BatteryConfiguration = batteryConfiguration,
            FeedInCompensationPricePerKwh = feedInCompensationPricePerKwh
        });
        var entries = SimulateEntries(
            priceForecast,
            pvForecast,
            consumptionForecast,
            initialBatteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh,
            plannedNegativePriceChargeSlots,
            needPlan);

        return new BatteryForecastResult(initialBatteryState, batteryConfiguration, entries);
    }

    private IReadOnlyList<BatteryForecastEntry> SimulateEntries(
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        IReadOnlyList<PvYieldForecastSlot> pvForecast,
        IReadOnlyList<ConsumptionForecastSlot> consumptionForecast,
        BatteryState initialBatteryState,
        BatteryConfiguration batteryConfiguration,
        decimal feedInCompensationPricePerKwh,
        IReadOnlySet<ForecastTimeSlot> plannedNegativePriceChargeSlots,
        BatteryGridChargeNeedPlan needPlan)
    {
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
                plannedNegativePriceChargeSlots.Contains(priceSlot.TimeSlot),
                needPlan.Find(priceSlot.TimeSlot),
                priceForecast,
                averagePricePerKwh,
                feedInCompensationPricePerKwh);

            currentBatteryEnergyKwh = simulatedSlot.BatteryEnergyAfterKwh;
            entries.Add(simulatedSlot.Entry);
        }

        return entries;
    }

    private static SimulatedSlot SimulateSlot(
        TibberPriceForecastSlot priceSlot,
        PvYieldForecastSlot pvSlot,
        ConsumptionForecastSlot consumptionSlot,
        decimal stateOfChargeBeforePercent,
        decimal batteryEnergyBeforeKwh,
        BatteryConfiguration batteryConfiguration,
        bool isPlannedNegativePriceChargeSlot,
        BatteryPlannedSlotAction? plannedAction,
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
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
                singleDirectionEfficiency,
                plannedAction);
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
            isPlannedNegativePriceChargeSlot,
            plannedAction,
            averagePricePerKwh,
            feedInCompensationPricePerKwh);
    }

    private static SimulatedSlot SimulatePvSurplusSlot(
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
        decimal singleDirectionEfficiency,
        BatteryPlannedSlotAction? plannedAction)
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

        // PV surplus rarely uses the full charge power, especially while the E3/DC takes PV first. A cheap PV slot can
        // therefore also be the cheapest grid charging slot; the planned grid energy fills the remaining charge power.
        var plannedGridInputKwh = plannedAction is { ChargeInputKwh: > 0m } && priceSlot.TotalPricePerKwh >= 0m
            ? CalculateChargeInputEnergyKwh(
                plannedAction.ChargeInputKwh,
                maximumChargeInputEnergyKwh - chargeInputEnergyKwh,
                availableCapacityKwh - storedEnergyKwh,
                singleDirectionEfficiency)
            : 0m;

        if (plannedGridInputKwh > EnergyToleranceKwh)
        {
            var totalStoredEnergyKwh = storedEnergyKwh + plannedGridInputKwh * singleDirectionEfficiency;

            return CreateSimulatedSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                expectedGridImportBeforeBatteryKwh,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh + totalStoredEnergyKwh,
                batteryConfiguration,
                new BatteryDecisionInstruction(BatteryDecisionState.Charge, BatteryChargeSource.Grid),
                CalculatePowerWatts(chargeInputEnergyKwh + plannedGridInputKwh, priceSlot.TimeSlot),
                plannedAction!.RuleId,
                $"Der Akku nimmt {chargeInputEnergyKwh:0.0000} kWh PV-Ueberschuss auf und laedt zusaetzlich {plannedGridInputKwh:0.0000} kWh aus dem Netz zu {priceSlot.TotalPricePerKwh:0.0000} EUR/kWh. {plannedAction.Explanation}");
        }

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

    private static SimulatedSlot SimulateTibberPriceSlot(
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
        bool isPlannedNegativePriceChargeSlot,
        BatteryPlannedSlotAction? plannedAction,
        decimal averagePricePerKwh,
        decimal feedInCompensationPricePerKwh)
    {
        var planningMinimumBatteryEnergyKwh = CalculatePlanningMinimumBatteryEnergyKwh(batteryConfiguration);

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

            if (isPlannedNegativePriceChargeSlot || !HasFutureCheaperNegativePrice(priceForecast, priceSlot))
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

        if (plannedAction is { ChargeInputKwh: > 0m })
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
                MaximumChargeInputEnergyKwh = Math.Min(maximumChargeInputEnergyKwh, plannedAction.ChargeInputKwh),
                SingleDirectionEfficiency = singleDirectionEfficiency,
                FeedInCompensationPricePerKwh = feedInCompensationPricePerKwh,
                RuleId = plannedAction.RuleId,
                ReasonMessage = $"Bedarfsgerechte Netzladung zu {priceSlot.TotalPricePerKwh:0.0000} EUR/kWh. {plannedAction.Explanation}"
            });
        }

        if (expectedGridImportBeforeBatteryKwh <= 0m)
        {
            return CreateIdleSlot(
                priceSlot,
                pvSlot,
                consumptionSlot,
                expectedGridImportBeforeBatteryKwh,
                stateOfChargeBeforePercent,
                batteryEnergyBeforeKwh,
                batteryConfiguration,
                BatteryForecastRuleIds.NeutralIdle,
                "Es wird kein Netzbezug erwartet und es ist keine Netzladung noetig. Die Decision Engine bleibt idle.");
        }

        if (batteryEnergyBeforeKwh <= planningMinimumBatteryEnergyKwh)
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

        // By default the battery covers consumption. A planned hold keeps part of that energy for a later, more expensive slot.
        var holdOutputKwh = plannedAction?.HoldOutputKwh ?? 0m;
        var allowedDischargeOutputKwh = Math.Max(
            0m,
            Math.Min(expectedGridImportBeforeBatteryKwh, maximumDischargeOutputEnergyKwh) - holdOutputKwh);

        if (allowedDischargeOutputKwh <= EnergyToleranceKwh)
        {
            // Without a planned hold this only happens for a negligible expected import, which needs no battery action.
            return plannedAction is null
                ? CreateIdleSlot(
                    priceSlot,
                    pvSlot,
                    consumptionSlot,
                    expectedGridImportBeforeBatteryKwh,
                    stateOfChargeBeforePercent,
                    batteryEnergyBeforeKwh,
                    batteryConfiguration,
                    BatteryForecastRuleIds.NeutralIdle,
                    "Der erwartete Netzbezug ist vernachlaessigbar klein. Die Decision Engine bleibt idle.")
                : CreateIdleSlot(
                    priceSlot,
                    pvSlot,
                    consumptionSlot,
                    expectedGridImportBeforeBatteryKwh,
                    stateOfChargeBeforePercent,
                    batteryEnergyBeforeKwh,
                    batteryConfiguration,
                    plannedAction.RuleId,
                    $"Der Akku haelt seine Energie zurueck; der Verbrauch wird zu {priceSlot.TotalPricePerKwh:0.0000} EUR/kWh aus dem Netz gedeckt. {plannedAction.Explanation}");
        }

        var isPartialHold = plannedAction is not null && holdOutputKwh > 0m;
        var dischargeRuleId = isPartialHold
            ? plannedAction!.RuleId
            : BatteryForecastRuleIds.SelfConsumptionDischarge;
        var dischargeReason = isPartialHold
            ? $"Der Akku deckt den Verbrauch nur teilweise und haelt {holdOutputKwh:0.0000} kWh zurueck. {plannedAction!.Explanation}"
            : "Der Akku deckt den erwarteten Verbrauch, damit kein Netzstrom gekauft werden muss.";

        return CreateDischargeSlot(
            priceSlot,
            pvSlot,
            consumptionSlot,
            stateOfChargeBeforePercent,
            batteryEnergyBeforeKwh,
            batteryConfiguration,
            expectedGridImportBeforeBatteryKwh,
            planningMinimumBatteryEnergyKwh,
            allowedDischargeOutputKwh,
            singleDirectionEfficiency,
            dischargeRuleId,
            dischargeReason);
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

        // A rest energy just above the reserve can round to 0 W, which is no valid discharge instruction.
        if (targetPowerWatts <= 0)
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
            new[] { new BatteryDecisionReason(ruleId, reasonMessage) },
            priceSlot.IsEstimated);

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
