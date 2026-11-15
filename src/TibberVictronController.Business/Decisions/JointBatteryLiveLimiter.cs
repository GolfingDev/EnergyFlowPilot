using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>Rebounds the plan against present energy, measured house load, device and phase limits.</summary>
public static class JointBatteryLiveLimiter
{
    // Below this, measurement noise and short PV spikes are not worth switching the batteries into charging.
    private const int SurplusThresholdWatts = 200;

    // Above any battery's limit on purpose; only used to read a battery's present discharge limit from the allocator.
    private const int FollowDemandRequestWatts = 100_000;

    /// <summary>
    /// Longest time a selected command may wait for its measurements. Above the 6 s cycle budget of one execution
    /// tick, so it never triggers in a healthy tick; it only keeps a command from being reused by a later tick.
    /// </summary>
    public static readonly TimeSpan CommandMaximumSelectionAge = TimeSpan.FromSeconds(10);

    public static JointBatteryCommand Limit(JointBatteryCommand desired, JointBatteryLiveSnapshot live, DateTimeOffset now) =>
        Limit(desired, live, now, now);

    /// <param name="now">Current time; measurement freshness is always judged against it.</param>
    /// <param name="commandSelectedAtUtc">
    /// Instant at which the tick selected <paramref name="desired"/>. The command's validity is judged against this
    /// instant, not against <paramref name="now"/>: reading both devices takes hundreds of milliseconds, and a quarter
    /// hour that ends during that read is no fault (in the field every such tick at hh:00/15/30/45 stopped both
    /// batteries for 30 s). The next tick selects the new quarter hour.
    /// </param>
    public static JointBatteryCommand Limit(JointBatteryCommand desired, JointBatteryLiveSnapshot live, DateTimeOffset now,
        DateTimeOffset commandSelectedAtUtc)
    {
        var limits = live.Limits;
        var commandExpired = commandSelectedAtUtc >= desired.ValidUntilUtc || commandSelectedAtUtc > now.AddSeconds(2) ||
            now - commandSelectedAtUtc > CommandMaximumSelectionAge;
        if (live.Batteries.Count != 2 || commandExpired || now - live.MeasuredAtUtc > TimeSpan.FromSeconds(5) || live.MeasuredAtUtc > now.AddSeconds(2) ||
            live.Batteries.Any(b => now - b.State.MeasuredAtUtc > TimeSpan.FromSeconds(5) || b.State.MeasuredAtUtc > now.AddSeconds(2)))
            throw new InvalidOperationException("Gemeinsamer Sollwert oder Messwerte sind abgelaufen.");
        if (limits.PhaseImportWatts.Count != 3 || limits.PhaseImportWatts.Any(p => p <= 0) || limits.VictronAcInputWatts <= 0 ||
            limits.VictronPhaseCount is < 1 or > 3 || live.PhaseGridWatts.Count != 3 || live.AcOutWatts.Count != limits.VictronPhaseCount)
            throw new InvalidOperationException("Bestaetigte Anschluss- und Phasengrenzen fehlen.");
        if (Math.Sign(desired.VictronWatts) * Math.Sign(desired.E3dcWatts) < 0)
            throw new InvalidOperationException("Gegenlaeufige Akku-Sollwerte sind unzulaessig.");

        // Site balance without both batteries ("base"): what the house and PV alone would exchange with the grid.
        var secondary = live.Batteries[1];
        var eta = (decimal)Math.Sqrt((double)secondary.Configuration.RoundTripEfficiencyPercent / 100d);
        var secondaryAc = secondary.MeasuredPowerWatts * (secondary.MeasuredPowerWatts >= 0 ? 1 / eta : eta);
        var basePhase = Enumerable.Range(0, 3).Select(i => live.PhaseGridWatts[i] -
            (i < limits.VictronPhaseCount ? live.PrimaryAcBatteryWatts / (decimal)limits.VictronPhaseCount : 0) - secondaryAc / 3m).ToArray();
        var baseTotal = basePhase.Sum();

        // PV surplus is stored instead of exported (operator report 24.10.2026: "hold" put the E3/DC into idle, which
        // also stops its own PV charging, and the surplus went to the grid). This is a live correction like the limits
        // below and also applies inside a committed quarter hour. Grid charging (planned or manual) is left unchanged.
        // Using the base balance keeps it stable: absorbing the surplus does not make the surplus disappear.
        var surplus = -baseTotal - 100;
        if (surplus > SurplusThresholdWatts && desired.ChargeSource != BatteryChargeSource.Grid)
        {
            var absorb = JointBatteryAllocator.Allocate(live.Batteries, (int)surplus, TimeSpan.FromSeconds(10), pvCharge: true);
            var victron = Math.Max(Math.Max(0, desired.VictronWatts), absorb[0].SignedPowerWatts);
            var e3dc = Math.Max(Math.Max(0, desired.E3dcWatts), absorb[1].SignedPowerWatts);
            if (victron + e3dc > 0)
                desired = desired with { VictronWatts = victron, E3dcWatts = e3dc, ChargeSource = BatteryChargeSource.PV,
                    Reason = desired.Reason + " PV-Ueberschuss wird gespeichert." };
        }
        else if (live.PhaseGridWatts.Sum() < -150 && live.Batteries.Any(b => b.MeasuredPowerWatts < -150))
            return desired with { VictronWatts = 0, E3dcWatts = 0, Reason = "Akkuentladung bei Netzeinspeisung gestoppt." };

        var targets = new[] { desired.VictronWatts, desired.E3dcWatts };
        for (var i = 0; i < 2; i++)
        {
            var unit = live.Batteries[i];
            var bound = JointBatteryAllocator.Allocate([unit], targets[i], TimeSpan.FromSeconds(10), desired.ChargeSource == BatteryChargeSource.PV)[0];
            targets[i] = bound.SignedPowerWatts;
        }
        var total = targets.Sum();
        decimal scale = 1;
        if (total > 0)
        {
            // Requested Victron watts are AC-side; E3/DC conversion uncertainty is covered conservatively.
            for (var phase = 0; phase < 3; phase++)
            {
                var addition = (phase < limits.VictronPhaseCount ? targets[0] / (decimal)limits.VictronPhaseCount : 0) + targets[1] / eta / 3m;
                if (addition > 0) scale = Math.Min(scale, Math.Max(0, limits.PhaseImportWatts[phase] - basePhase[phase] - 150) / addition);
            }
            var availableAc = live.AcOutWatts.Select(load => Math.Max(0, limits.VictronAcInputWatts - load - 100)).Min() * limits.VictronPhaseCount;
            if (targets[0] > availableAc) targets[0] = availableAc;
            var acAddition = targets[0] + targets[1] / eta;
            if (limits.TotalImportWatts > 0 && acAddition > 0) scale = Math.Min(scale, Math.Max(0, limits.TotalImportWatts - baseTotal - 150) / acAddition);
            if (desired.ChargeSource == BatteryChargeSource.PV && acAddition > 0) scale = Math.Min(scale, Math.Max(0, -baseTotal - 100) / acAddition);
        }
        else if (total < 0)
        {
            var demandScale = Math.Max(0, baseTotal - 100) / -total;
            if (desired.FollowsDemand && demandScale > 1 && targets.All(t => t <= 0))
                return FollowDemand(desired, live, targets, demandScale);
            scale = Math.Min(scale, demandScale);
        }
        scale = Math.Clamp(scale, 0, 1);
        return desired with { VictronWatts = (int)(targets[0] * scale), E3dcWatts = (int)(targets[1] * scale) };
    }

    /// <summary>
    /// The measured demand is above the planned discharge of a slot whose decision is "cover the house demand" (the
    /// plan saw a smaller load up to a plan interval ago). The shares are the planned watts after each battery's own
    /// bound, exactly as when a discharge is scaled down. Each battery is raised in that proportion, so the sum never
    /// exceeds base load minus the 100 W margin - the same bound as for every discharge - and each battery stays
    /// within its own power limit, active device limit and energy above its reserve. What a raised battery cannot
    /// deliver beyond that limit is not moved to the other one: the next plan redistributes with full knowledge. A
    /// battery the plan left at 0 W (or that is at its reserve) has no share and stays at 0 W.
    /// </summary>
    private static JointBatteryCommand FollowDemand(JointBatteryCommand desired, JointBatteryLiveSnapshot live, int[] targets, decimal demandScale)
    {
        var raised = new int[2];
        for (var i = 0; i < 2; i++)
        {
            if (targets[i] == 0) continue;
            // Same per-battery bound as above, asked for more than any battery can deliver: its discharge limit now.
            var limit = JointBatteryAllocator.Allocate([live.Batteries[i]], -FollowDemandRequestWatts, TimeSpan.FromSeconds(10))[0].SignedPowerWatts;
            raised[i] = (int)Math.Max(limit, targets[i] * demandScale);
        }
        return desired with { VictronWatts = raised[0], E3dcWatts = raised[1] };
    }
}
