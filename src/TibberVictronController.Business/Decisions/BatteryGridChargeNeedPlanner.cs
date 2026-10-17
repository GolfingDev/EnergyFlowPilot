using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Plans grid charging and battery holding from the expected energy need instead of fixed price phases.
/// </summary>
/// <remarks>
/// The battery covers consumption by default. Grid import then only remains in slots where the battery is expected to be empty.
/// Each such shortage is closed with the cheapest earlier source: holding battery energy in a cheaper slot (costs that slot's price)
/// or charging from the grid (costs the charge price divided by the round-trip efficiency). A source is only used when it is clearly
/// cheaper than the shortage slot and when the extra energy does not displace expected PV surplus. Grid charging without such a need
/// would only buy energy that PV or the battery itself would have provided anyway.
/// </remarks>
internal sealed class BatteryGridChargeNeedPlanner
{
    private const decimal EnergyToleranceKwh = 0.001m;
    private const int MaximumPlanningIterations = 400;

    /// <summary>
    /// Repeatedly simulates the forecast and adds the cheapest profitable hold or charge action until no shortage can be closed.
    /// </summary>
    public BatteryGridChargeNeedPlan Plan(
        BatteryConfiguration batteryConfiguration,
        Func<BatteryGridChargeNeedPlan, IReadOnlyList<BatteryForecastEntry>> simulate)
    {
        var plan = new BatteryGridChargeNeedPlan();

        // Every iteration re-simulates because closing one shortage changes the state of charge of all later slots.
        // There is no end-of-horizon reserve: the horizon end only reflects when prices are published, and missing prices
        // are estimated from a comparable day, so needs after midnight are planned like any other shortage.
        for (var iteration = 0; iteration < MaximumPlanningIterations; iteration++)
        {
            var slots = AnalyzeSlots(simulate(plan), batteryConfiguration, plan);

            if (!TryCloseMostExpensiveShortage(slots, plan, batteryConfiguration))
            {
                break;
            }
        }

        return plan;
    }

    private static bool TryCloseMostExpensiveShortage(
        IReadOnlyList<AnalyzedSlot> slots,
        BatteryGridChargeNeedPlan plan,
        BatteryConfiguration batteryConfiguration)
    {
        var shortageSlotIndexes = slots
            .Where(slot => slot.EnergyShortageKwh > EnergyToleranceKwh)
            .OrderByDescending(slot => slot.PricePerKwh)
            .ThenBy(slot => slot.Index)
            .Select(slot => slot.Index);

        foreach (var shortageSlotIndex in shortageSlotIndexes)
        {
            var shortageSlot = slots[shortageSlotIndex];
            var explanation =
                $"Ohne diese Aktion waere der Akku um {shortageSlot.Entry.TimeSlot.StartsAtUtc:HH:mm} UTC leer und dort wuerden {shortageSlot.PricePerKwh:0.0000} EUR/kWh aus dem Netz bezogen.";
            var request = new AllocationRequest(
                shortageSlotIndex,
                shortageSlot.EnergyShortageKwh,
                shortageSlot.PricePerKwh - batteryConfiguration.MinimumSavingsPerKwh,
                explanation,
                BatteryForecastRuleIds.HoldForExpensiveSlot);

            if (TryAllocate(slots, plan, batteryConfiguration, request))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAllocate(
        IReadOnlyList<AnalyzedSlot> slots,
        BatteryGridChargeNeedPlan plan,
        BatteryConfiguration batteryConfiguration,
        AllocationRequest request)
    {
        var singleDirectionEfficiency = CalculateSingleDirectionEfficiency(batteryConfiguration);
        var roundTripEfficiency = singleDirectionEfficiency * singleDirectionEfficiency;
        var neededStoredEnergyKwh = request.NeededOutputEnergyKwh / singleDirectionEfficiency;
        var candidates = new List<AllocationCandidate>();
        var minimumHeadroomKwh = decimal.MaxValue;

        // Energy added in slot j stays in the battery until the shortage slot, so every slot in between needs headroom.
        // Sources before the last slot in which the battery runs out of energy cannot help, because their energy is used up there.
        // A slot that is only limited by discharge power does not consume additional energy, so the search continues past it.
        for (var index = request.ShortageSlotIndex - 1; index >= 0; index--)
        {
            var slot = slots[index];
            minimumHeadroomKwh = Math.Min(minimumHeadroomKwh, slot.HeadroomKwh);

            if (minimumHeadroomKwh <= EnergyToleranceKwh)
            {
                break;
            }

            if (slot.DischargeOutputKwh > EnergyToleranceKwh)
            {
                var holdOutputKwh = Math.Min(
                    Math.Min(slot.DischargeOutputKwh, request.NeededOutputEnergyKwh),
                    minimumHeadroomKwh * singleDirectionEfficiency);
                candidates.Add(new AllocationCandidate(slot, AllocationKind.Hold, slot.PricePerKwh, holdOutputKwh));
            }
            else if (slot.CanGridCharge)
            {
                var chargeInputKwh = Math.Min(
                    Math.Min(slot.RemainingChargeInputKwh, neededStoredEnergyKwh / singleDirectionEfficiency),
                    minimumHeadroomKwh / singleDirectionEfficiency);
                // Holding shifts an existing discharge; grid charging adds a full cycle, so only it carries the wear cost.
                candidates.Add(new AllocationCandidate(slot, AllocationKind.Charge, slot.PricePerKwh / roundTripEfficiency + batteryConfiguration.WearCostPerKwh, chargeInputKwh));
            }

            if (slot.IsEnergyLimited)
            {
                break;
            }
        }

        var bestCandidate = candidates
            .Where(candidate => candidate.EnergyKwh > EnergyToleranceKwh && candidate.CostPerKwh < request.MaximumCostPerKwh)
            .OrderBy(candidate => candidate.CostPerKwh)
            .ThenBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.Slot.Index)
            .FirstOrDefault();

        if (bestCandidate is null)
        {
            return false;
        }

        var ruleId = bestCandidate.Kind == AllocationKind.Hold
            ? BatteryForecastRuleIds.HoldForExpensiveSlot
            : BatteryForecastRuleIds.PlannedGridCharge;

        if (bestCandidate.Kind == AllocationKind.Hold)
        {
            plan.AddHold(bestCandidate.Slot.Entry.TimeSlot, bestCandidate.EnergyKwh, ruleId, request.Explanation);
        }
        else
        {
            plan.AddCharge(bestCandidate.Slot.Entry.TimeSlot, bestCandidate.EnergyKwh, ruleId, request.Explanation);
        }

        return true;
    }

    private static IReadOnlyList<AnalyzedSlot> AnalyzeSlots(
        IReadOnlyList<BatteryForecastEntry> entries,
        BatteryConfiguration batteryConfiguration,
        BatteryGridChargeNeedPlan plan)
    {
        var planningMinimumBatteryEnergyKwh = batteryConfiguration.TotalCapacityKwh * batteryConfiguration.PlanningMinimumStateOfChargePercent / 100m;
        var planningMaximumBatteryEnergyKwh = batteryConfiguration.TotalCapacityKwh * batteryConfiguration.PlanningMaximumStateOfChargePercent / 100m;

        return entries
            .Select((entry, index) =>
            {
                var slotHours = (decimal)entry.TimeSlot.Duration.TotalHours;
                var energyAfterKwh = batteryConfiguration.TotalCapacityKwh * entry.StateOfChargeAfterPercent / 100m;
                var isDischarge = entry.Decision.Instruction.DecisionState == BatteryDecisionState.Discharge;
                var isCharge = entry.Decision.Instruction.DecisionState == BatteryDecisionState.Charge;
                var dischargeOutputKwh = isDischarge ? entry.Decision.TargetPowerWatts / 1000m * slotHours : 0m;
                var plannedAction = plan.Find(entry.TimeSlot);
                var isPlanned = plannedAction is not null;
                var isBatteryEmpty = energyAfterKwh <= planningMinimumBatteryEnergyKwh + EnergyToleranceKwh;
                var maximumDischargeOutputKwh = batteryConfiguration.MaximumDischargePowerWatts / 1000m * slotHours;
                var holdOutputKwh = plannedAction?.HoldOutputKwh ?? 0m;
                var uncoveredImportKwh = Math.Min(Math.Max(0m, entry.ExpectedGridImportBeforeBatteryKwh), maximumDischargeOutputKwh)
                    - holdOutputKwh
                    - dischargeOutputKwh;
                var isEnergyLimited = !isCharge && isBatteryEmpty && uncoveredImportKwh > EnergyToleranceKwh;

                // Only import caused by an empty battery is a shortage; intentional holds and charge slots are planned on purpose.
                var energyShortageKwh = isEnergyLimited && !isPlanned
                    ? uncoveredImportKwh
                    : 0m;
                var maximumChargeInputKwh = batteryConfiguration.MaximumChargePowerWatts / 1000m * slotHours;

                // PV surplus slots stay grid charge candidates; only the charge power not used by PV (or by an earlier
                // planned grid charge) is available. The larger value also stops repeated allocations to a slot the
                // simulator cannot charge in, e.g. while headroom is kept for negative prices.
                var usedChargeInputKwh = Math.Max(
                    isCharge ? entry.Decision.TargetPowerWatts / 1000m * slotHours : 0m,
                    plannedAction?.ChargeInputKwh ?? 0m);

                return new AnalyzedSlot(
                    index,
                    entry,
                    entry.TibberPricePerKwh,
                    energyAfterKwh,
                    planningMaximumBatteryEnergyKwh - energyAfterKwh,
                    dischargeOutputKwh,
                    energyShortageKwh,
                    isEnergyLimited,
                    // Grid charging is only planned on published prices; estimated slots may hold energy but never buy it.
                    CanGridCharge: entry.TibberPricePerKwh >= 0m && !entry.IsPriceEstimated,
                    RemainingChargeInputKwh: Math.Max(0m, maximumChargeInputKwh - usedChargeInputKwh));
            })
            .ToArray();
    }

    private static decimal CalculateSingleDirectionEfficiency(BatteryConfiguration batteryConfiguration)
    {
        return (decimal)Math.Sqrt((double)(batteryConfiguration.RoundTripEfficiencyPercent / 100m));
    }

    private enum AllocationKind
    {
        Hold,
        Charge
    }

    private sealed record AnalyzedSlot(
        int Index,
        BatteryForecastEntry Entry,
        decimal PricePerKwh,
        decimal EnergyAfterKwh,
        decimal HeadroomKwh,
        decimal DischargeOutputKwh,
        decimal EnergyShortageKwh,
        bool IsEnergyLimited,
        bool CanGridCharge,
        decimal RemainingChargeInputKwh);

    private sealed record AllocationRequest(
        int ShortageSlotIndex,
        decimal NeededOutputEnergyKwh,
        decimal MaximumCostPerKwh,
        string Explanation,
        string RuleId);

    private sealed record AllocationCandidate(
        AnalyzedSlot Slot,
        AllocationKind Kind,
        decimal CostPerKwh,
        decimal EnergyKwh);
}

/// <summary>
/// Holds the planned hold and grid charge actions per forecast slot.
/// </summary>
internal sealed class BatteryGridChargeNeedPlan
{
    private readonly Dictionary<ForecastTimeSlot, BatteryPlannedSlotAction> actions = new();

    public BatteryPlannedSlotAction? Find(ForecastTimeSlot timeSlot)
    {
        return actions.TryGetValue(timeSlot, out var action) ? action : null;
    }

    public void AddHold(ForecastTimeSlot timeSlot, decimal holdOutputKwh, string ruleId, string explanation)
    {
        var current = Find(timeSlot);
        actions[timeSlot] = new BatteryPlannedSlotAction(
            (current?.HoldOutputKwh ?? 0m) + holdOutputKwh,
            current?.ChargeInputKwh ?? 0m,
            ruleId,
            explanation);
    }

    public void AddCharge(ForecastTimeSlot timeSlot, decimal chargeInputKwh, string ruleId, string explanation)
    {
        var current = Find(timeSlot);
        actions[timeSlot] = new BatteryPlannedSlotAction(
            current?.HoldOutputKwh ?? 0m,
            (current?.ChargeInputKwh ?? 0m) + chargeInputKwh,
            ruleId,
            explanation);
    }
}

/// <summary>
/// Describes how much battery output is held back and how much grid energy is charged in one forecast slot.
/// </summary>
internal sealed record BatteryPlannedSlotAction(
    decimal HoldOutputKwh,
    decimal ChargeInputKwh,
    string RuleId,
    string Explanation);
