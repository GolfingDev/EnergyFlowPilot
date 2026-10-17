using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

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
