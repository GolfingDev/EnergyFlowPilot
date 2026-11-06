namespace TibberVictronController.Dal.Entities;

public sealed class PvForecastSnapshotEntity
{
    public DateTimeOffset SlotStartUtc { get; set; }
    public double ForecastKwh { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

public sealed class PriceForecastSnapshotEntity
{
    public DateTimeOffset SlotStartUtc { get; set; }
    public int LeadDays { get; set; }
    public double SpotPricePerKwh { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

public sealed class UserCalendarEntity
{
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Mailbox { get; set; } = string.Empty;
    public DateTimeOffset ConnectedAtUtc { get; set; }
}

public sealed class CarSampleEntity
{
    public long Id { get; set; }
    public DateTimeOffset MeasuredAtUtc { get; set; }
    public double? SocPercent { get; set; }
    public double? OdometerKm { get; set; }
}
