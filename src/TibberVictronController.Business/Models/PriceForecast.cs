namespace TibberVictronController.Business.Models;

/// <summary>
/// One quarter hour of the price forecast service: exchange price in EUR/kWh (without tariff, levies or VAT) and
/// whether it is still a forecast or already the published day-ahead auction result.
/// </summary>
public sealed record PriceForecastPoint(DateTimeOffset SlotStartUtc, decimal SpotPricePerKwh, bool IsForecast);

/// <summary>
/// Stored forecast of one quarter hour: the latest one per whole day of lead time (0 = less than a day ahead), so the
/// accuracy can be judged by how far ahead it was made. <see cref="Actual"/> holds the published exchange price.
/// </summary>
public sealed record PriceForecastSnapshot(DateTimeOffset SlotStartUtc, int LeadDays, decimal SpotPricePerKwh, DateTimeOffset CapturedAtUtc)
{
    public const int Actual = -1;
}
