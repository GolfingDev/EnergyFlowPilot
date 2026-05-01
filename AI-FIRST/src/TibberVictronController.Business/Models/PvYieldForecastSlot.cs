namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents the expected PV yield for one forecast slot.
/// </summary>
public sealed record PvYieldForecastSlot
{
    /// <summary>
    /// Prevents impossible negative PV yield values from entering the Decision Engine forecast.
    /// </summary>
    public PvYieldForecastSlot(ForecastTimeSlot timeSlot, decimal expectedPvYieldKwh)
    {
        if (expectedPvYieldKwh < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedPvYieldKwh), "Der erwartete PV-Ertrag darf nicht negativ sein.");
        }

        TimeSlot = timeSlot;
        ExpectedPvYieldKwh = expectedPvYieldKwh;
    }

    /// <summary>
    /// Gets the forecast slot this PV estimate belongs to.
    /// </summary>
    public ForecastTimeSlot TimeSlot { get; }

    /// <summary>
    /// Gets the expected PV yield for the slot.
    /// </summary>
    public decimal ExpectedPvYieldKwh { get; }
}
