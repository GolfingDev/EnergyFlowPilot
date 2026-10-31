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
}
