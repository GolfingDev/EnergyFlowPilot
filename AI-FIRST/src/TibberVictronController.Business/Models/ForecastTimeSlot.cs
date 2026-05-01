namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents one UTC time slot used by Decision Engine forecast input and output models.
/// </summary>
public sealed record ForecastTimeSlot
{
    /// <summary>
    /// Validates the Decision Engine invariant that forecast time ranges are expressed in UTC and move forward.
    /// </summary>
    public ForecastTimeSlot(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Die Startzeit des Forecast-Zeitabschnitts muss in UTC angegeben sein.", nameof(startsAtUtc));
        }

        if (endsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Die Endzeit des Forecast-Zeitabschnitts muss in UTC angegeben sein.", nameof(endsAtUtc));
        }

        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("Die Endzeit des Forecast-Zeitabschnitts muss nach der Startzeit liegen.", nameof(endsAtUtc));
        }

        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
    }

    /// <summary>
    /// Gets the UTC slot start.
    /// </summary>
    public DateTimeOffset StartsAtUtc { get; }

    /// <summary>
    /// Gets the UTC slot end.
    /// </summary>
    public DateTimeOffset EndsAtUtc { get; }

    /// <summary>
    /// Gets the slot duration so tests and services can verify the required 15-minute forecast grid.
    /// </summary>
    public TimeSpan Duration => EndsAtUtc - StartsAtUtc;

    /// <summary>
    /// Returns whether this slot matches the forecast granularity required by the Decision Engine.
    /// </summary>
    public bool IsFifteenMinuteSlot => Duration == TimeSpan.FromMinutes(15);
}
