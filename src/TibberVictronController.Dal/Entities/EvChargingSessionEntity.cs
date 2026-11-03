namespace TibberVictronController.Dal.Entities;

public sealed class EvChargingSessionEntity
{
    public int TransactionId { get; set; }
    public string ChargePointId { get; set; } = string.Empty;
    public int ConnectorId { get; set; }
    public string IdTag { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public double MeterStartWh { get; set; }
    public DateTimeOffset? StoppedAtUtc { get; set; }
    public double? MeterStopWh { get; set; }
    public string? StopReason { get; set; }
    public double? LastMeterWh { get; set; }
    public DateTimeOffset? LastMeterAtUtc { get; set; }
    // Answer to "charge now or by price?" below the base state of charge (Immediate / PriceDriven), null while open.
    public string? BaseChargeMode { get; set; }
    public DateTimeOffset? BaseChoiceAskedAtUtc { get; set; }
    public string? BaseChoiceBy { get; set; }
    public bool IsStoppedByUser { get; set; }
    public string? StoppedBy { get; set; }
}
