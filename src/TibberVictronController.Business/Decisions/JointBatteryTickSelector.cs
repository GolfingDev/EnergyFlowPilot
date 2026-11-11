using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>How the command of one execution tick came about; the caller logs and remembers accordingly.</summary>
public enum JointBatteryTickOutcome
{
    /// <summary>The committed quarter hour continues with the watts and shares of a fresh same-action plan.</summary>
    UpdatedTargets,
    /// <summary>A vanished PV surplus is replaced by the economically planned discharge of a fresh plan.</summary>
    NetDemandReplacement,
    /// <summary>The committed quarter hour continues unchanged.</summary>
    Committed,
    /// <summary>The committed quarter hour continues although a fresh plan proposes a notably different action.</summary>
    CommittedDespiteDeviation,
    /// <summary>A new quarter hour starts with the current plan slot as its committed basic action.</summary>
    NewCommitment,
    /// <summary>No usable plan: self-consumption without grid charging.</summary>
    SelfConsumptionFallback
}

/// <param name="PlanUnavailableReason">Why the planner has no plan (its blocked message), if it reported one.</param>
public sealed record JointBatteryTickInput(DateTimeOffset NowUtc, JointBatterySlot? CommittedSlot, JointBatteryPlan? Plan,
    string? PlanUnavailableReason);

/// <param name="CommittedSlot">The committed slot after this tick (unchanged in the fallback).</param>
/// <param name="DeviatingSlot">The fresh plan slot that deviates from the committed one.</param>
public sealed record JointBatteryTickSelection(JointBatteryTickOutcome Outcome, JointBatteryCommand Command,
    JointBatterySlot? CommittedSlot, JointBatterySlot? PreviousCommittedSlot = null, JointBatterySlot? DeviatingSlot = null,
    string? FallbackCause = null);

/// <summary>
/// Chooses the joint command of one execution tick from the committed quarter hour and the latest plan (manual charges
/// are handled before). Only the basic action of a quarter hour is fixed; a fresh same-action plan updates watts and
/// shares. Missing, stale or unusable plans never leave the quarter hour without an action (operator rule 29.10.2026):
/// a running commitment continues, otherwise self-consumption. Live limits, reserves and direction changes are applied
/// afterwards by <see cref="JointBatteryLiveLimiter"/> and <see cref="JointBatteryExecutionCoordinator"/>.
/// </summary>
public static class JointBatteryTickSelector
{
    public static readonly TimeSpan PlanMaximumAge = TimeSpan.FromSeconds(75);

    // Above any battery's limit on purpose; the per-battery allocation in the live limiter applies the real limits.
    private const int SelfConsumptionRequestWatts = 100_000;

    public static JointBatteryTickSelection Select(JointBatteryTickInput input)
    {
        var now = input.NowUtc;
        var plan = input.Plan;
        if (JointBatterySlotCommitment.Active(input.CommittedSlot, now) is { } committed)
        {
            if (JointBatterySlotCommitment.UpdatedTargets(committed, plan, now) is { } updated)
                return new(JointBatteryTickOutcome.UpdatedTargets, CommandFrom(updated), updated, committed);
            if (JointBatterySlotCommitment.NetDemandReplacement(committed, plan, now) is { } replacement)
                return new(JointBatteryTickOutcome.NetDemandReplacement, CommandFrom(replacement), replacement, committed);
            return JointBatterySlotCommitment.PlanDeviates(committed, plan)
                ? new(JointBatteryTickOutcome.CommittedDespiteDeviation, CommandFrom(committed), committed, committed,
                    plan!.Slots.First(s => s.TimeSlot == committed.TimeSlot))
                : new(JointBatteryTickOutcome.Committed, CommandFrom(committed), committed, committed);
        }

        var fallbackCause = plan is null ? input.PlanUnavailableReason ?? "Die Planung laeuft noch."
            : now - plan.CalculatedAtUtc > PlanMaximumAge ? "Die gemeinsame Planung ist veraltet."
            : null;
        var slot = fallbackCause is null
            ? plan!.Slots.SingleOrDefault(s => s.TimeSlot.StartsAtUtc <= now && s.TimeSlot.EndsAtUtc > now)
            : null;
        if (fallbackCause is null && slot is null) fallbackCause = "Der Plan enthaelt die aktuelle Viertelstunde nicht.";
        if (fallbackCause is null && slot!.IsPriceEstimated && slot.ChargeSource == BatteryChargeSource.Grid)
            fallbackCause = "Geschaetzte Preise erlauben keine Netzladung.";
        if (fallbackCause is not null)
            return new(JointBatteryTickOutcome.SelfConsumptionFallback, SelfConsumption(now, fallbackCause), input.CommittedSlot,
                FallbackCause: fallbackCause);

        return new(JointBatteryTickOutcome.NewCommitment, CommandFrom(slot!), slot);
    }

    /// <summary>
    /// Self-consumption without a plan: both batteries are asked to discharge at their limits; the live limiter cuts
    /// this down to the measured house demand (never export), keeps every reserve, and turns it into PV charging when
    /// there is a surplus. No grid charging. Short validity: it is rebuilt every step until a plan is available again.
    /// </summary>
    private static JointBatteryCommand SelfConsumption(DateTimeOffset now, string cause) =>
        new(-SelfConsumptionRequestWatts, -SelfConsumptionRequestWatts, null, now.AddSeconds(15),
            "Notbetrieb ohne gueltigen Plan: Hausbedarf aus den Akkus decken, PV-Ueberschuss speichern, keine Netzladung. Ursache: " + cause);

    // Valid until the end of its quarter hour; every step rebuilds and re-limits the command from live data anyway.
    private static JointBatteryCommand CommandFrom(JointBatterySlot slot) =>
        new(slot.Targets.Single(t => t.Id == "victron").SignedPowerWatts, slot.Targets.Single(t => t.Id == "e3dc").SignedPowerWatts,
            slot.ChargeSource, slot.TimeSlot.EndsAtUtc, slot.Reason);
}
