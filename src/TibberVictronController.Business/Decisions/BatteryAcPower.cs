namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Battery power as it appears on the AC side of the site balance. Both batteries report their power on the DC side
/// (Victron: battery monitor topic ".../Dc/0/Power"; E3/DC: RSCP EMS_POWER_BAT). Charging draws more from the AC side
/// than reaches the battery, discharging delivers less. Subtracting the DC value from the grid power would therefore
/// count the conversion losses as house demand in exactly the slots in which the planner charges or discharges, and
/// the planner would then apply the efficiency a second time.
/// </summary>
public static class BatteryAcPower
{
    /// <summary>
    /// Converts a DC-side battery power (positive = charging) with the single-direction efficiency, the square root
    /// of the round-trip efficiency. Deliberately the same formula as <c>JointBatteryLiveLimiter.Limit</c> uses for
    /// the E3/DC ("secondaryAc"); the limiter keeps its own copy, so a change here must be mirrored there.
    /// </summary>
    public static decimal FromDcSide(decimal dcWatts, decimal roundTripEfficiencyPercent)
    {
        // An unusable efficiency must not turn a measurement into nonsense; the unconverted value is the old behaviour.
        if (roundTripEfficiencyPercent is <= 0 or > 100) return dcWatts;
        var eta = (decimal)Math.Sqrt((double)roundTripEfficiencyPercent / 100d);
        return dcWatts * (dcWatts >= 0 ? 1 / eta : eta);
    }
}
