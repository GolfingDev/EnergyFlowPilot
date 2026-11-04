namespace TibberVictronController.Business.Models;

/// <summary>
/// One reading of the E3/DC battery from one source. Power is signed like the Victron battery: positive charges,
/// negative discharges.
/// </summary>
public sealed record SupplementaryBatteryReading(DateTimeOffset MeasuredAtUtc, decimal? StateOfChargePercent, decimal? PowerWatts)
{
    /// <summary>
    /// The newest reading that is not older than <paramref name="maximumAge"/>. The E3/DC is read by several services at
    /// very different rates (local RSCP every second during joint control, every 30 s for the joint preview, the cloud
    /// every minute); only the newest one says what the battery does now.
    /// </summary>
    public static SupplementaryBatteryReading? Newest(DateTimeOffset nowUtc, TimeSpan maximumAge, params SupplementaryBatteryReading?[] candidates) =>
        candidates
            .OfType<SupplementaryBatteryReading>()
            .Where(reading => nowUtc - reading.MeasuredAtUtc <= maximumAge)
            .OrderByDescending(reading => reading.MeasuredAtUtc)
            .FirstOrDefault();
}
