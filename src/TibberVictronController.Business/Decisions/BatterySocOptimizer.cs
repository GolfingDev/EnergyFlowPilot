using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// One forecast slot as seen by the optimizer: price and the expected grid import before the battery acts.
/// </summary>
/// <param name="NetLoadKwh">Expected consumption minus expected PV yield; negative values are PV surplus.</param>
internal sealed record BatterySocOptimizationSlot(
    ForecastTimeSlot TimeSlot,
    decimal PricePerKwh,
    bool IsPriceEstimated,
    decimal NetLoadKwh);

internal sealed class BatterySocOptimizationInput
{
    public required IReadOnlyList<BatterySocOptimizationSlot> Slots { get; init; }

    public required decimal InitialEnergyKwh { get; init; }

    public decimal PrepaidGridEnergyKwh { get; init; }

    public required BatteryConfiguration BatteryConfiguration { get; init; }

    public required decimal FeedInCompensationPricePerKwh { get; init; }
}

/// <summary>
/// Plans holding and grid charging by minimising the energy cost of the whole horizon with dynamic programming over the
/// stored battery energy.
/// </summary>
/// <remarks>
/// The previous greedy planner closed one shortage at a time and could miss combinations, e.g. holding energy early so a
/// cheaper charge later suffices. Here every slot chooses its next battery energy on a 0.05 kWh grid between planning minimum
/// and maximum; the backward pass stores the cheapest remaining cost per energy level, the forward pass follows it from the
/// real start energy. The physical rules match the simulator: PV surplus is always stored, the battery never discharges more
/// than the expected import (no feed-in from the battery), and grid charging needs a published, non-negative price.
/// Newly deferred energy relative to a fixed self-consumption baseline costs the configured minimum savings per kWh once;
/// simply waiting with the same deferred energy is free. Grid charging has its own minimum-advantage hurdle and additionally
/// carries the battery wear cost. Energy left at the horizon end has no value, as before there is no end reserve.
/// The result uses the existing plan format, so the simulator stays the model for SoC projection and explanations.
/// </remarks>
internal sealed class BatterySocOptimizer
{
    private const double EnergyStepKwh = 0.05d;
    private const double ActionToleranceKwh = 0.01d;
    private const double Epsilon = 1e-9d;

    public BatteryGridChargeNeedPlan Plan(BatterySocOptimizationInput input)
    {
        var plan = new BatteryGridChargeNeedPlan();
        var configuration = input.BatteryConfiguration;
        var minimumEnergyKwh = (double)(configuration.TotalCapacityKwh * configuration.PlanningMinimumStateOfChargePercent / 100m);
        var maximumEnergyKwh = (double)(configuration.TotalCapacityKwh * configuration.PlanningMaximumStateOfChargePercent / 100m);

        if (input.Slots.Count == 0 || maximumEnergyKwh - minimumEnergyKwh < EnergyStepKwh)
        {
            return plan;
        }

        var grid = new EnergyGrid(minimumEnergyKwh, maximumEnergyKwh);
        // The reference trajectory covers consumption/stores PV without price-based holds or grid purchases.
        // It lets Evaluate charge newly deferred energy, rather than charging the same stock every quarter hour.
        // Measured grid purchases already paid the economic hurdle in an earlier decision cycle.
        var baselineEnergyKwh = Math.Max(minimumEnergyKwh,
            (double)(input.InitialEnergyKwh - Math.Max(0m, input.PrepaidGridEnergyKwh)));
        var models = input.Slots.Select(slot =>
        {
            var model = SlotModel.Create(slot, input);
            var baselineNextKwh = model.GetNextEnergyRange(baselineEnergyKwh, minimumEnergyKwh, maximumEnergyKwh).Lower;
            model = model with { BaselineBeforeKwh = baselineEnergyKwh, BaselineAfterKwh = baselineNextKwh };
            baselineEnergyKwh = baselineNextKwh;
            return model;
        }).ToArray();

        // Backward pass: cheapest cost from slot t until the horizon end for every stored energy level.
        var values = new double[models.Length + 1][];
        values[models.Length] = new double[grid.Count];
        for (var slotIndex = models.Length - 1; slotIndex >= 0; slotIndex--)
        {
            var nextValues = values[slotIndex + 1];
            var slotValues = new double[grid.Count];

            for (var level = 0; level < grid.Count; level++)
            {
                slotValues[level] = ChooseTransition(models[slotIndex], grid.EnergyAt(level), grid, nextValues).TotalCost;
            }

            values[slotIndex] = slotValues;
        }

        // Forward pass from the real start energy, which does not have to lie on the grid.
        var energyKwh = (double)input.InitialEnergyKwh;
        var transitions = new Transition[models.Length];
        for (var slotIndex = 0; slotIndex < models.Length; slotIndex++)
        {
            transitions[slotIndex] = ChooseTransition(models[slotIndex], energyKwh, grid, values[slotIndex + 1]);
            energyKwh = transitions[slotIndex].NextEnergyKwh;
        }

        AddActions(plan, input.Slots, models, transitions);

        return plan;
    }

    private static void AddActions(
        BatteryGridChargeNeedPlan plan,
        IReadOnlyList<BatterySocOptimizationSlot> slots,
        IReadOnlyList<SlotModel> models,
        IReadOnlyList<Transition> transitions)
    {
        for (var slotIndex = 0; slotIndex < transitions.Count; slotIndex++)
        {
            var model = models[slotIndex];
            var transition = transitions[slotIndex];

            if (transition.GridChargeInputKwh > ActionToleranceKwh)
            {
                plan.AddCharge(
                    slots[slotIndex].TimeSlot,
                    (decimal)transition.GridChargeInputKwh,
                    BatteryForecastRuleIds.PlannedGridCharge,
                    CreateExplanation(slots, transitions, slotIndex));
                continue;
            }

            // The simulator measures a hold against the uncapped discharge, min(import, maximum discharge power).
            var heldOutputKwh = model.DefaultOutputKwh(transition.StartEnergyKwh) - transition.OutputKwh;
            if (heldOutputKwh > ActionToleranceKwh)
            {
                plan.AddHold(
                    slots[slotIndex].TimeSlot,
                    (decimal)(Math.Min(model.ImportKwh, model.MaximumOutputKwh) - transition.OutputKwh),
                    BatteryForecastRuleIds.HoldForExpensiveSlot,
                    CreateExplanation(slots, transitions, slotIndex));
            }
        }
    }

    /// <summary>
    /// Names the most expensive later slot in which the battery covers consumption, which is where shifted energy pays off.
    /// </summary>
    private static string CreateExplanation(
        IReadOnlyList<BatterySocOptimizationSlot> slots,
        IReadOnlyList<Transition> transitions,
        int slotIndex)
    {
        var target = Enumerable.Range(slotIndex + 1, slots.Count - slotIndex - 1)
            .Where(index => transitions[index].OutputKwh > ActionToleranceKwh)
            .OrderByDescending(index => slots[index].PricePerKwh)
            .ThenBy(index => index)
            .Select(index => slots[index])
            .FirstOrDefault();

        return target is null
            ? "Der Optimierer verschiebt Akkuenergie in teurere Zeiten."
            : $"Der Optimierer nutzt die Energie spaeter: um {target.TimeSlot.StartsAtUtc:HH:mm} UTC deckt der Akku Verbrauch zu {target.PricePerKwh:0.0000} EUR/kWh.";
    }

    private static Transition ChooseTransition(SlotModel model, double energyKwh, EnergyGrid grid, double[] nextValues)
    {
        var (lowerKwh, upperKwh) = model.GetNextEnergyRange(energyKwh, grid.MinimumKwh, grid.MaximumKwh);
        var best = Evaluate(model, energyKwh, lowerKwh, grid, nextValues);

        // Ties keep the earlier candidate; the default behaviour (lower bound) is evaluated first.
        void Consider(double nextEnergyKwh)
        {
            if (nextEnergyKwh < lowerKwh - Epsilon || nextEnergyKwh > upperKwh + Epsilon)
            {
                return;
            }

            var candidate = Evaluate(model, energyKwh, nextEnergyKwh, grid, nextValues);
            if (candidate.TotalCost < best.TotalCost - Epsilon)
            {
                best = candidate;
            }
        }

        Consider(energyKwh);
        for (var level = grid.FirstLevelAbove(lowerKwh); level < grid.Count && grid.EnergyAt(level) < upperKwh; level++)
        {
            Consider(grid.EnergyAt(level));
        }

        Consider(upperKwh);

        return best;
    }

    private static Transition Evaluate(SlotModel model, double energyKwh, double nextEnergyKwh, EnergyGrid grid, double[] nextValues)
    {
        var deltaKwh = nextEnergyKwh - energyKwh;
        double gridKwh;
        double gridChargeInputKwh = 0d;
        double outputKwh = 0d;

        if (deltaKwh >= 0d)
        {
            var chargeInputKwh = deltaKwh / model.Efficiency;
            gridKwh = model.NetLoadKwh + chargeInputKwh;
            gridChargeInputKwh = Math.Max(0d, chargeInputKwh - model.SurplusKwh);
        }
        else
        {
            outputKwh = -deltaKwh * model.Efficiency;
            gridKwh = model.NetLoadKwh - outputKwh;
        }

        var energyCost = gridKwh >= 0d ? gridKwh * model.PricePerKwh : gridKwh * model.FeedInPricePerKwh;
        var deferredBeforeKwh = Math.Max(0d, energyKwh - model.BaselineBeforeKwh);
        var deferredAfterKwh = Math.Max(0d, nextEnergyKwh - model.BaselineAfterKwh);
        // Grid purchases already carry the hurdle below; don't also charge their stored increment as a hold.
        var newlyDeferredKwh = Math.Max(0d, deferredAfterKwh - deferredBeforeKwh - gridChargeInputKwh * model.Efficiency);
        var deviationCost = gridChargeInputKwh * (model.MinimumSavingsPerKwh + model.WearCostPerKwh * model.Efficiency)
            + newlyDeferredKwh * model.Efficiency * model.MinimumSavingsPerKwh;

        return new Transition(
            energyKwh,
            nextEnergyKwh,
            outputKwh,
            gridChargeInputKwh,
            energyCost + deviationCost + grid.Interpolate(nextValues, nextEnergyKwh));
    }

    private readonly record struct Transition(
        double StartEnergyKwh,
        double NextEnergyKwh,
        double OutputKwh,
        double GridChargeInputKwh,
        double TotalCost);

    private sealed record SlotModel(
        double NetLoadKwh,
        double PricePerKwh,
        double FeedInPricePerKwh,
        double MaximumInputKwh,
        double MaximumOutputKwh,
        double Efficiency,
        bool CanGridCharge,
        double MinimumSavingsPerKwh,
        double WearCostPerKwh,
        double MinimumEnergyKwh)
    {
        public double BaselineBeforeKwh { get; init; }
        public double BaselineAfterKwh { get; init; }

        public double ImportKwh => Math.Max(0d, NetLoadKwh);

        public double SurplusKwh => Math.Max(0d, -NetLoadKwh);

        public static SlotModel Create(BatterySocOptimizationSlot slot, BatterySocOptimizationInput input)
        {
            var configuration = input.BatteryConfiguration;
            var slotHours = slot.TimeSlot.Duration.TotalHours;

            return new SlotModel(
                (double)slot.NetLoadKwh,
                (double)slot.PricePerKwh,
                (double)input.FeedInCompensationPricePerKwh,
                configuration.MaximumChargePowerWatts / 1000d * slotHours,
                configuration.MaximumDischargePowerWatts / 1000d * slotHours,
                Math.Sqrt((double)(configuration.RoundTripEfficiencyPercent / 100m)),
                // Grid charging is only planned on published prices; estimated slots may hold energy but never buy it.
                slot.PricePerKwh >= 0m && !slot.IsPriceEstimated,
                (double)configuration.MinimumSavingsPerKwh,
                (double)configuration.WearCostPerKwh,
                (double)(configuration.TotalCapacityKwh * configuration.PlanningMinimumStateOfChargePercent / 100m));
        }

        /// <summary>
        /// Output of the default behaviour: cover the import as far as power and energy above the planning minimum allow.
        /// </summary>
        public double DefaultOutputKwh(double energyKwh)
        {
            return Math.Min(Math.Min(ImportKwh, MaximumOutputKwh), Math.Max(0d, energyKwh - MinimumEnergyKwh) * Efficiency);
        }

        /// <summary>
        /// Lower bound: the default behaviour (full self-consumption or storing all PV surplus that fits). Upper bound: grid
        /// charging with the remaining charge power when allowed, otherwise the same as without battery action.
        /// </summary>
        public (double Lower, double Upper) GetNextEnergyRange(double energyKwh, double minimumKwh, double maximumKwh)
        {
            double lower;
            double upper;

            if (SurplusKwh > 0d)
            {
                var pvInputKwh = Math.Min(Math.Min(SurplusKwh, MaximumInputKwh), Math.Max(0d, maximumKwh - energyKwh) / Efficiency);
                lower = energyKwh + pvInputKwh * Efficiency;
                upper = lower;
            }
            else
            {
                lower = energyKwh - DefaultOutputKwh(energyKwh) / Efficiency;
                upper = energyKwh;
            }

            if (CanGridCharge)
            {
                upper = Math.Max(upper, Math.Min(maximumKwh, energyKwh + MaximumInputKwh * Efficiency));
            }

            return (lower, Math.Max(lower, upper));
        }
    }

    /// <summary>
    /// Stored energy levels from planning minimum to planning maximum; the last step may be shorter.
    /// </summary>
    private sealed class EnergyGrid
    {
        public EnergyGrid(double minimumKwh, double maximumKwh)
        {
            MinimumKwh = minimumKwh;
            MaximumKwh = maximumKwh;
            Count = (int)Math.Ceiling((maximumKwh - minimumKwh) / EnergyStepKwh - Epsilon) + 1;
        }

        public double MinimumKwh { get; }

        public double MaximumKwh { get; }

        public int Count { get; }

        public double EnergyAt(int level)
        {
            return Math.Min(MinimumKwh + level * EnergyStepKwh, MaximumKwh);
        }

        public int FirstLevelAbove(double energyKwh)
        {
            return Math.Max(0, (int)Math.Floor((energyKwh - MinimumKwh) / EnergyStepKwh + Epsilon) + 1);
        }

        /// <summary>
        /// Linear interpolation between levels; energies outside the planning range use the nearest level.
        /// </summary>
        public double Interpolate(double[] values, double energyKwh)
        {
            if (energyKwh <= MinimumKwh)
            {
                return values[0];
            }

            if (energyKwh >= MaximumKwh)
            {
                return values[Count - 1];
            }

            var level = Math.Min(Count - 2, (int)Math.Floor((energyKwh - MinimumKwh) / EnergyStepKwh));
            var lowerEnergyKwh = EnergyAt(level);
            var upperEnergyKwh = EnergyAt(level + 1);
            var fraction = upperEnergyKwh - lowerEnergyKwh <= Epsilon ? 0d : (energyKwh - lowerEnergyKwh) / (upperEnergyKwh - lowerEnergyKwh);

            return values[level] + (values[level + 1] - values[level]) * fraction;
        }
    }
}
