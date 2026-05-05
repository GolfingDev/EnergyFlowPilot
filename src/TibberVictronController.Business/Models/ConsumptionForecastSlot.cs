namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents expected household consumption for one forecast slot.
/// </summary>
public sealed record ConsumptionForecastSlot
{
    /// <summary>
    /// Prevents impossible negative consumption values from entering the Decision Engine forecast.
    /// </summary>
    public ConsumptionForecastSlot(ForecastTimeSlot timeSlot, decimal expectedConsumptionKwh)
    {
        if (expectedConsumptionKwh < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedConsumptionKwh), "Der erwartete Verbrauch darf nicht negativ sein.");
        }

        TimeSlot = timeSlot;
        ExpectedConsumptionKwh = expectedConsumptionKwh;
    }

    /// <summary>
    /// Gets the forecast slot this consumption estimate belongs to.
    /// </summary>
    public ForecastTimeSlot TimeSlot { get; }

    /// <summary>
    /// Gets the expected consumption for the slot.
    /// </summary>
    public decimal ExpectedConsumptionKwh { get; }
}
