namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents a Tibber electricity price for a single forecast slot.
/// </summary>
public sealed record TibberPriceForecastSlot
{
    /// <summary>
    /// Guards the external price contract before the Decision Engine uses the value for decisions or forecasts.
    /// </summary>
    public TibberPriceForecastSlot(ForecastTimeSlot timeSlot, decimal totalPricePerKwh, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException("Die Waehrung des Strompreises muss angegeben werden.", nameof(currency));
        }

        TimeSlot = timeSlot;
        TotalPricePerKwh = totalPricePerKwh;
        Currency = currency;
    }

    /// <summary>
    /// Gets the forecast slot this price belongs to.
    /// </summary>
    public ForecastTimeSlot TimeSlot { get; }

    /// <summary>
    /// Gets the total Tibber price per kWh for the slot.
    /// </summary>
    public decimal TotalPricePerKwh { get; }

    /// <summary>
    /// Gets the price currency.
    /// </summary>
    public string Currency { get; }
}
