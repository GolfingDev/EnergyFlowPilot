using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Fixes the basic action of a running quarter hour, not its watts or allocation between batteries.
/// Fresh same-action plans may update both targets; all live limits still apply on every execution step.
/// </summary>
public static class JointBatterySlotCommitment
{
    /// <returns>The committed slot if its quarter hour is still running, otherwise null (a new decision is due).</returns>
    public static JointBatterySlot? Active(JointBatterySlot? committed, DateTimeOffset nowUtc) =>
        committed is not null && committed.TimeSlot.StartsAtUtc <= nowUtc && nowUtc < committed.TimeSlot.EndsAtUtc ? committed : null;

    /// <summary>Refresh power and allocation without changing direction or the permitted charging source.</summary>
    public static JointBatterySlot? UpdatedTargets(JointBatterySlot committed, JointBatteryPlan? plan, DateTimeOffset nowUtc)
    {
        if (Active(committed, nowUtc) is null || plan is null || plan.CalculatedAtUtc > nowUtc ||
            nowUtc - plan.CalculatedAtUtc > TimeSpan.FromSeconds(75)) return null;
        var fresh = plan.Slots.FirstOrDefault(s => s.TimeSlot == committed.TimeSlot);
        if (fresh is null || fresh.Targets.Count != committed.Targets.Count ||
            !committed.Targets.All(t => fresh.Targets.Count(f => f.Id == t.Id) == 1)) return null;
        var direction = Math.Sign(committed.Targets.Sum(t => t.SignedPowerWatts));
        if (Math.Sign(fresh.Targets.Sum(t => t.SignedPowerWatts)) != direction ||
            fresh.Targets.Any(t => Math.Sign(t.SignedPowerWatts) != 0 && Math.Sign(t.SignedPowerWatts) != direction) ||
            fresh.ChargeSource != committed.ChargeSource ||
            (fresh.IsPriceEstimated && fresh.ChargeSource == BatteryChargeSource.Grid)) return null;
        // Zero in a new plan means a different basic action (hold), not permission to cancel a started action.
        // Actual power can still fall to zero through reserve, demand, or hardware limits in the live limiter.
        return fresh;
    }

    /// <summary>
    /// A PV assumption must not lock out an economically justified discharge after real net demand appears.
    /// Reuse the live-corrected planner decision, rather than inventing a price threshold or overriding a grid charge.
    /// </summary>
    public static JointBatterySlot? NetDemandReplacement(JointBatterySlot committed, JointBatteryPlan? plan, DateTimeOffset nowUtc)
    {
        if (Active(committed, nowUtc) is null || committed.ChargeSource == BatteryChargeSource.Grid ||
            committed.Targets.Any(t => t.SignedPowerWatts < 0) ||
            committed.ExpectedConsumptionKwh > committed.ExpectedPvKwh || plan is null ||
            plan.CalculatedAtUtc > nowUtc || nowUtc - plan.CalculatedAtUtc > TimeSpan.FromSeconds(75))
            return null;
        var fresh = plan.Slots.FirstOrDefault(s => s.TimeSlot == committed.TimeSlot);
        // 200 W avoids reacting to tiny balance changes. Device/SoC/export limits still apply at execution time.
        if (fresh is null || fresh.ExpectedConsumptionKwh - fresh.ExpectedPvKwh <= .05m ||
            fresh.ChargeSource is not null || fresh.Targets.Count != committed.Targets.Count ||
            !committed.Targets.All(t => fresh.Targets.Count(f => f.Id == t.Id) == 1) ||
            fresh.Targets.Any(t => t.SignedPowerWatts > 0) || fresh.Targets.Sum(t => t.SignedPowerWatts) >= -200)
            return null;
        return fresh with { Reason = "Die PV reicht aktuell nicht fuer den Hausbedarf. " + fresh.Reason };
    }

    /// <summary>True when a newer plan proposes a notably different action for the committed quarter hour.</summary>
    public static bool PlanDeviates(JointBatterySlot committed, JointBatteryPlan? plan)
    {
        var fresh = plan?.Slots.FirstOrDefault(s => s.TimeSlot == committed.TimeSlot);
        if (fresh is null) return false;
        return committed.Targets.Any(target =>
            Math.Abs(target.SignedPowerWatts - (fresh.Targets.FirstOrDefault(t => t.Id == target.Id)?.SignedPowerWatts ?? 0)) > 100);
    }
}
