namespace TibberVictronController.Business.EvCharging;

public enum EvChargingMode
{
    Off,
    Waiting,
    Plan,
    PvSurplus,
    Paused,
    FullPowerFallback
}

/// <summary>Amps null means: no limit, the wallbox charges at its own (full) setting.</summary>
public sealed record EvChargingDecision(EvChargingMode Mode, double? Amps, string Reason);

/// <summary>
/// AverageAvailableWatts: grid export plus the car's own charging power minus home battery discharge, averaged over
/// the last minutes, i.e. what the car could take without importing from the grid or emptying the home batteries
/// (see <see cref="EvSurplusSample"/>). Null when live data is missing.
/// </summary>
public sealed record EvChargingDecisionInput(
    DateTimeOffset NowUtc,
    EvChargePlan? Plan,
    string? PlanProblem,
    double? AverageAvailableWatts)
{
    /// <summary>Grid power accepted to charge at the minimum current when the surplus is slightly too small.</summary>
    public double PvGridAllowanceWatts { get; init; }

    /// <summary>Whether surplus charging runs and since when; without it every cycle decides as if freshly started.</summary>
    public EvSurplusChargingState Surplus { get; init; } = EvSurplusChargingState.None;
}

/// <summary>
/// Chooses the charging current for the next cycle: the planned quarter hour or live PV surplus, whichever is more;
/// otherwise pause. Without a usable plan the wallbox gets no limit (rule 1: never stand still for missing data).
/// </summary>
public static class EvChargingDecider
{
    public const double VoltsTimesPhases = 230 * 3;
    public const double MinimumAmps = 6;
    public const double MaximumAmps = 16;
    // Kept free so that small load changes in the house do not immediately pull from the grid. It is also the
    // hysteresis of surplus charging: starting needs this margin on top, continuing does not.
    public const double SurplusMarginWatts = 300;
    // During the minimum run time a passing cloud may be bridged from the grid or the home batteries, but only while
    // the car's minimum power lacks at most this much on average. A deeper drop stops at once, so the minimum run
    // time cannot turn into battery or grid charging (at most 2 kW for 10 minutes, about 0.33 kWh per start).
    public const double MinimumRunShortfallLimitWatts = 2000;

    public static EvChargingDecision Decide(EvChargingDecisionInput input)
    {
        if (input.Plan is not { HasInputSlots: true } plan)
            return new EvChargingDecision(EvChargingMode.FullPowerFallback, null,
                $"Kein Ladeplan ({input.PlanProblem ?? "keine Preise"}): volle Leistung, damit das Auto sicher geladen wird.");

        var slot = plan.Slots.FirstOrDefault(slot => slot.StartsAtUtc <= input.NowUtc && input.NowUtc < slot.EndsAtUtc);
        // PV-only quarter hours follow the live surplus instead of the forecast: the home batteries take the PV first,
        // the car gets what is fed in. For a coming trip the car charges as planned, ahead of the home batteries
        // (operator 2026-11-06).
        double? planAmps = slot is { GridKwh: > 0 } or { Reason: EvChargeReason.Trip } ? Clamp(slot.PowerWatts / VoltsTimesPhases) : null;
        var (surplusAmps, surplusNote, surplusBlocked) = Surplus(input);

        if (planAmps is null && surplusAmps is null)
        {
            var next = plan.Slots.FirstOrDefault(candidate => candidate.StartsAtUtc > input.NowUtc && candidate.GridKwh > 0);
            // Waiting for unpublished prices must not read as "nothing needed" (operator 2026-11-11).
            return new EvChargingDecision(EvChargingMode.Paused, 0, surplusBlocked ?? (next is not null
                ? $"Pause bis zum nächsten geplanten Ladeblock ({next.StartsAtUtc:yyyy-MM-dd HH:mm} UTC{(next.IsPriceEstimated ? ", Preis geschätzt" : string.Empty)})."
                : plan.Goals.Any(goal => goal.WaitsForPrices)
                    ? "Pause: wartet auf die Preise der kommenden Nacht, um den Bedarf günstig zu laden."
                    : "Pause: kein Ladebedarf im Plan und kein PV-Überschuss."));
        }

        if ((surplusAmps ?? 0) > (planAmps ?? 0))
            return new EvChargingDecision(EvChargingMode.PvSurplus, surplusAmps, $"PV-Überschuss: {surplusAmps:0} A{surplusNote}.");
        return new EvChargingDecision(EvChargingMode.Plan, planAmps,
            $"Geplanter Ladeblock {ReasonText(slot!.Reason)} ({slot.PricePerKwh * 100:0.0} ct/kWh): {planAmps:0} A.");
    }

    /// <summary>
    /// The current the live PV surplus allows. Only this branch has hysteresis, minimum run time and minimum pause;
    /// planned charging (grid quarter hours, trips) is decided without them and wins whenever it is at least as high.
    /// </summary>
    private static (double? Amps, string? Note, string? Blocked) Surplus(EvChargingDecisionInput input)
    {
        if (input.AverageAvailableWatts is not { } available || available <= 0) return default;
        // What charging at the minimum current would take from the grid or the home batteries.
        var shortfall = MinimumAmps * VoltsTimesPhases - available;
        // The current follows the surplus so that (almost) nothing comes from the grid; just below the minimum, six
        // amps are accepted if the grid share stays within the allowance.
        var amps = Clamp(Math.Floor((available - SurplusMarginWatts) / VoltsTimesPhases));
        var state = input.Surplus;
        if (!state.IsCharging)
        {
            // Starting needs the margin on top, so a surplus right at the limit does not start and stop in turns.
            if (shortfall > input.PvGridAllowanceWatts - SurplusMarginWatts) return default;
            return state.MinimumPauseEndsAtUtc is { } pauseEnd && input.NowUtc < pauseEnd
                ? (null, null, $"Pause: PV-Überschuss vorhanden, Mindestpause nach dem letzten Ladestopp bis {pauseEnd:HH:mm} UTC.")
                : (amps, null, null);
        }

        if (shortfall <= input.PvGridAllowanceWatts) return (amps, null, null);
        return state.MinimumRunEndsAtUtc is { } runEnd && input.NowUtc < runEnd && shortfall <= MinimumRunShortfallLimitWatts
            ? (MinimumAmps, $" (Überschuss derzeit zu gering, Mindestlaufzeit bis {runEnd:HH:mm} UTC)", null)
            : default;
    }

    public static string ReasonText(EvChargeReason reason) => reason switch
    {
        EvChargeReason.Trip => "für eine Fahrt",
        EvChargeReason.PvSurplus => "mit PV-Überschuss",
        EvChargeReason.BaseSoc => "bis zum Grund-Ladestand",
        EvChargeReason.ImmediateTarget => "bis zum Sofort-Ladeziel",
        _ => "bei günstigem Strom"
    };

    private static double Clamp(double amps) => Math.Round(Math.Clamp(amps, MinimumAmps, MaximumAmps), 0, MidpointRounding.AwayFromZero);
}
