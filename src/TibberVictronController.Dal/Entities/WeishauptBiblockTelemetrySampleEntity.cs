namespace TibberVictronController.Dal.Entities;

public sealed class WeishauptBiblockTelemetrySampleEntity
{
    public long Id { get; set; }
    public DateTimeOffset MeasuredAtUtc { get; set; }
    public decimal OutdoorTemperatureCelsius { get; set; }
    public ushort OperatingStatusCode { get; set; }
    public decimal HotWaterTemperatureCelsius { get; set; }
}
