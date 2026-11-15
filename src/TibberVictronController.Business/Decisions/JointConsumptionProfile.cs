using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>Round-trip efficiencies used to convert the stored DC-side battery powers to the AC side.</summary>
public sealed record JointProfileEfficiencies(decimal PrimaryRoundTripPercent, decimal SecondaryRoundTripPercent);

/// <summary>Reconstructs gross house demand without learning either battery's action as consumption.</summary>
public static class JointConsumptionProfile
{
    // The wallbox is subtracted: car charging is planned separately and must not be learned as house demand.
    // With efficiencies the battery powers are converted to the AC side first (see BatteryAcPower); without them the
    // raw values are used, which counts conversion losses as house demand.
    public static decimal? GrossWatts(LiveConsumptionSample sample, JointProfileEfficiencies? efficiencies = null) => sample is {
        GridPowerWatts: { } grid, BatteryPowerWatts: { } primary,
        SupplementaryBatteryPowerWatts: { } secondary, SupplementaryPvProductionWatts: { } pv
    } ? Math.Max(0, grid
        - (efficiencies is null ? primary : BatteryAcPower.FromDcSide(primary, efficiencies.PrimaryRoundTripPercent))
        - (efficiencies is null ? secondary : BatteryAcPower.FromDcSide(secondary, efficiencies.SecondaryRoundTripPercent))
        + pv - (sample.EvChargingWatts ?? 0)) : null;

    /// <summary>
    /// Samples whose wallbox power is unknown although a wallbox reports to this system. Stored values: a number
    /// (0 while no car charges) when the wallbox state is known, null when the wallbox is offline or a charge runs
    /// without a current reading - an 11 kW charge would then be learned as house demand. Null is also what every
    /// sample from before the wallbox was connected holds (and every sample of a site without a wallbox); those must
    /// stay usable. The stored data cannot tell the two apart per sample, so the first sample with a value marks the
    /// start of wallbox reporting: nulls before it are history without a wallbox, nulls after it are unknown.
    /// Limit: a wallbox outage that already runs when the learning window starts looks like history without a wallbox.
    /// </summary>
    public static IReadOnlyList<LiveConsumptionSample> WithoutUnknownWallboxPower(IReadOnlyList<LiveConsumptionSample> samples)
    {
        DateTimeOffset? reportingSince = null;
        foreach (var sample in samples)
            if (sample.EvChargingWatts is not null && (reportingSince is null || sample.MeasuredAtUtc < reportingSince))
                reportingSince = sample.MeasuredAtUtc;
        if (reportingSince is not { } since) return samples;
        return samples.Where(s => s.EvChargingWatts is not null || s.MeasuredAtUtc < since).ToArray();
    }

    public static ConsumptionForecastModel Learn(IReadOnlyList<LiveConsumptionSample> samples,
        TimeZoneInfo zone, DateTimeOffset now, IReadOnlyList<HourlyTemperature>? temperatures = null,
        JointProfileEfficiencies? efficiencies = null)
    {
        var averages = WithoutUnknownWallboxPower(samples)
            .Select(s => (Time: TimeZoneInfo.ConvertTime(s.MeasuredAtUtc, zone), Watts: GrossWatts(s, efficiencies)))
            .Where(s => s.Watts is >= 0 and <= 100000)
            .GroupBy(s => (Date: DateOnly.FromDateTime(s.Time.DateTime), Slot: s.Time.Hour * 4 + s.Time.Minute / 15))
            .Select(g => new ConsumptionDaySlotAverage(g.Key.Date, g.Key.Slot, g.Average(s => s.Watts!.Value))).ToArray();
        // Existing long-term summaries contain the independently controlled E3/DC effect and cannot be reused here.
        return ConsumptionForecastModel.Build(averages, temperatures is null ? new Dictionary<DateOnly, decimal>() :
            ConsumptionForecastModel.CalculateHeatingDegrees(temperatures, zone),
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime));
    }
}
