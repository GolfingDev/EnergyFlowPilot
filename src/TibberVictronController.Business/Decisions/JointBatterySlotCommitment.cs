using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Operator rule (24.10.2026): once execution of a quarter hour has begun, its plan decision is fixed until the
/// quarter hour ends. The plan is recalculated every 30 s and could otherwise flip a started charge on small input
/// changes. The only deviations are the live limits enforced on every step (SoC bounds, phase/feed-line/device limits,
/// no export by discharge) - those act on the fixed decision, they do not replace it.
/// </summary>
public static class JointBatterySlotCommitment
{
    /// <returns>The committed slot if its quarter hour is still running, otherwise null (a new decision is due).</returns>
    public static JointBatterySlot? Active(JointBatterySlot? committed, DateTimeOffset nowUtc) =>
        committed is not null && committed.TimeSlot.StartsAtUtc <= nowUtc && nowUtc < committed.TimeSlot.EndsAtUtc ? committed : null;

    /// <summary>True when a newer plan proposes a notably different action for the committed quarter hour.</summary>
    public static bool PlanDeviates(JointBatterySlot committed, JointBatteryPlan? plan)
    {
        var fresh = plan?.Slots.FirstOrDefault(s => s.TimeSlot == committed.TimeSlot);
        if (fresh is null) return false;
        return committed.Targets.Any(target =>
            Math.Abs(target.SignedPowerWatts - (fresh.Targets.FirstOrDefault(t => t.Id == target.Id)?.SignedPowerWatts ?? 0)) > 100);
    }
}
