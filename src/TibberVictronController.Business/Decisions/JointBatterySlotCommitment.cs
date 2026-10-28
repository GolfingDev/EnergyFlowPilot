using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Operator rule (24.10.2026): once execution of a quarter hour has begun, its plan decision is fixed until the
/// quarter hour ends. The plan is recalculated every 30 s and could otherwise flip a started charge on small input
/// changes. A fresh economically planned discharge may replace a vanished PV surplus. Other deviations are the live limits enforced on every step (SoC bounds, phase/feed-line/device limits,
/// no export by discharge) - those act on the fixed decision, they do not replace it.
/// </summary>
public static class JointBatterySlotCommitment
{
    /// <returns>The committed slot if its quarter hour is still running, otherwise null (a new decision is due).</returns>
    public static JointBatterySlot? Active(JointBatterySlot? committed, DateTimeOffset nowUtc) =>
        committed is not null && committed.TimeSlot.StartsAtUtc <= nowUtc && nowUtc < committed.TimeSlot.EndsAtUtc ? committed : null;

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
