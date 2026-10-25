using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Dashboard;

/// <summary>Display-only projection of both batteries; never changes primary-battery commands or accounting.</summary>
public static class JointBatteryDashboardOverview
{
    public static DashboardDecisionOverview Create(JointBatteryPlan? plan, JointBatteryExecutionSnapshot execution,
        DateTimeOffset now, bool manualCharge = false)
    {
        var slot = plan?.Slots.FirstOrDefault(s => s.TimeSlot.StartsAtUtc <= now && s.TimeSlot.EndsAtUtc > now);
        if (execution.Status != "Active" || now - execution.UpdatedAtUtc > TimeSpan.FromSeconds(5) || execution.ValidUntilUtc is not { } until || until <= now ||
            plan is null || now - plan.CalculatedAtUtc > TimeSpan.FromSeconds(75))
            return new(DashboardActionCategory.Idle, "Die gemeinsame Steuerung wartet auf eine gültige Freigabe. " + execution.Message,
                slot?.PricePerKwh, slot?.Currency, null);
        var watts = execution.VictronTargetWatts + execution.E3dcTargetWatts;
        var currentCategory = watts > 0 ? (manualCharge || slot?.ChargeSource != BatteryChargeSource.PV ? DashboardActionCategory.GridCharge : DashboardActionCategory.PvCharge)
            : watts < 0 ? DashboardActionCategory.Discharge : slot is null ? DashboardActionCategory.Idle : HoldingCategory(slot, plan);
        var next = manualCharge ? null : plan.Slots.FirstOrDefault(s => s.TimeSlot.StartsAtUtc > now && Category(s, plan) != currentCategory);
        var reason = watts == 0 && slot?.Targets.Any(t => t.SignedPowerWatts != 0) == true
            ? "Aktuell pausieren die Speicher. Die folgende Begründung beschreibt die geplante Akkuaktion: " + execution.Message
            : execution.Message;
        return new(currentCategory, reason, slot?.PricePerKwh, slot?.Currency,
            next is null ? null : new(next.TimeSlot.StartsAtUtc, Category(next, plan), next.Reason, next.PricePerKwh, next.Currency, next.IsPriceEstimated));
    }

    private static DashboardActionCategory Category(JointBatterySlot slot, JointBatteryPlan plan)
    {
        var watts = slot.Targets.Sum(t => t.SignedPowerWatts);
        if (watts > 0) return slot.ChargeSource == BatteryChargeSource.PV ? DashboardActionCategory.PvCharge : DashboardActionCategory.GridCharge;
        if (watts < 0) return DashboardActionCategory.Discharge;
        return HoldingCategory(slot, plan);
    }

    private static DashboardActionCategory HoldingCategory(JointBatterySlot slot, JointBatteryPlan plan) =>
        slot.Targets.Any(t => plan.Batteries.Any(b => b.Id == t.Id && t.SocBeforePercent > b.Configuration.PlanningMinimumStateOfChargePercent + .1m))
            ? DashboardActionCategory.Hold : DashboardActionCategory.Idle;
}
