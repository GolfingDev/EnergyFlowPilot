namespace TibberVictronController.Business.EvCharging;

public sealed record WallboxMeterSample(DateTimeOffset AtUtc, double MeterWh);

/// <summary>
/// BMW reports the state of charge only now and then; while charging it can stay silent for hours (operator
/// 2026-11-03: 26 % reported for 1.5 hours while the car charged to 35 %). The energy measured by the wallbox since
/// that report, after charging losses, is added, so the plan stops at its target.
/// </summary>
public static class EvSocEstimator
{
    /// <summary>Energy charged into the battery since the reported state of charge, in kWh.</summary>
    public static double ChargedSinceKwh(DateTimeOffset socAtUtc, DateTimeOffset sessionStartUtc, double meterStartWh,
        IReadOnlyList<WallboxMeterSample> samples, double meterNowWh)
    {
        var meterAtSoc = socAtUtc <= sessionStartUtc ? meterStartWh : MeterAt(socAtUtc, sessionStartUtc, meterStartWh, samples);
        return Math.Max(0, meterNowWh - meterAtSoc) / 1000d * EvTripSocPlanner.ChargeEfficiency;
    }

    // Linear between the known meter readings around the time (the session start counts as one); after the last known
    // reading the meter is taken as unchanged since, which leans towards charging a little less.
    private static double MeterAt(DateTimeOffset atUtc, DateTimeOffset sessionStartUtc, double meterStartWh, IReadOnlyList<WallboxMeterSample> samples)
    {
        var points = samples.Where(sample => sample.AtUtc > sessionStartUtc).OrderBy(sample => sample.AtUtc)
            .Prepend(new WallboxMeterSample(sessionStartUtc, meterStartWh)).ToArray();
        var before = points.Last(point => point.AtUtc <= atUtc);
        var after = points.FirstOrDefault(point => point.AtUtc > atUtc);
        if (after is null) return before.MeterWh;
        var share = (atUtc - before.AtUtc).TotalSeconds / (after.AtUtc - before.AtUtc).TotalSeconds;
        return before.MeterWh + share * (after.MeterWh - before.MeterWh);
    }
}
