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
/// AverageAvailableWatts: grid export plus the car's own charging power, averaged over the last minutes, i.e. what
/// the car could take without importing from the grid. Null when live grid data is missing.
/// </summary>
public sealed record EvChargingDecisionInput(
    DateTimeOffset NowUtc,
    EvChargePlan? Plan,
    string? PlanProblem,
    double? AverageAvailableWatts)
{
    /// <summary>Grid power accepted to charge at the minimum current when the surplus is slightly too small.</summary>
    public double PvGridAllowanceWatts { get; init; }
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
    // Kept free so that small load changes in the house do not immediately pull from the grid.
    private const double SurplusMarginWatts = 300;

    public static EvChargingDecision Decide(EvChargingDecisionInput input)
    {
        if (input.Plan is not { HasInputSlots: true } plan)
            return new EvChargingDecision(EvChargingMode.FullPowerFallback, null,
                $"Kein Ladeplan ({input.PlanProblem ?? "keine Preise"}): volle Leistung, damit das Auto sicher geladen wird.");

        var slot = plan.Slots.FirstOrDefault(slot => slot.StartsAtUtc <= input.NowUtc && input.NowUtc < slot.EndsAtUtc);
        // PV-only quarter hours follow the live surplus instead of the forecast.
        double? planAmps = slot is { GridKwh: > 0 } ? Clamp(slot.PowerWatts / VoltsTimesPhases) : null;
        // The current follows the surplus so that (almost) nothing comes from the grid; just below the minimum, six amps
        // are accepted if the grid share stays within the allowance.
        double? surplusAmps = input.AverageAvailableWatts switch
        {
            { } available when (available - SurplusMarginWatts) / VoltsTimesPhases >= MinimumAmps =>
                Clamp(Math.Floor((available - SurplusMarginWatts) / VoltsTimesPhases)),
            { } available when available > 0 && available + input.PvGridAllowanceWatts >= MinimumAmps * VoltsTimesPhases => MinimumAmps,
            _ => null
        };

        if (planAmps is null && surplusAmps is null)
        {
            var next = plan.Slots.FirstOrDefault(candidate => candidate.StartsAtUtc > input.NowUtc && candidate.GridKwh > 0);
            return new EvChargingDecision(EvChargingMode.Paused, 0, next is null
                ? "Pause: kein Ladebedarf im Plan und kein PV-Überschuss."
                : $"Pause bis zum nächsten geplanten Ladeblock ({next.StartsAtUtc:yyyy-MM-dd HH:mm} UTC).");
        }

        if ((surplusAmps ?? 0) > (planAmps ?? 0))
            return new EvChargingDecision(EvChargingMode.PvSurplus, surplusAmps, $"PV-Überschuss: {surplusAmps:0} A.");
        return new EvChargingDecision(EvChargingMode.Plan, planAmps,
            $"Geplanter Ladeblock ({slot!.PricePerKwh * 100:0.0} ct/kWh): {planAmps:0} A.");
    }

    private static double Clamp(double amps) => Math.Round(Math.Clamp(amps, MinimumAmps, MaximumAmps), 0, MidpointRounding.AwayFromZero);
}
