namespace TibberVictronController.Dal.Entities;

public sealed class WeishauptBiblockTelemetrySampleEntity
{
    public long Id { get; set; }
    public DateTimeOffset MeasuredAtUtc { get; set; }
    public decimal OutdoorTemperatureCelsius { get; set; }
    public ushort OperatingStatusCode { get; set; }
    public decimal HotWaterTemperatureCelsius { get; set; }

    // Unsigned whole-kWh device counters; null means unavailable, never zero consumption.
    public ushort? TotalHeatTodayKwh { get; set; }
    public ushort? TotalHeatYesterdayKwh { get; set; }
    public ushort? TotalHeatMonthKwh { get; set; }
    public ushort? TotalHeatYearKwh { get; set; }
    public ushort? HeatingHeatTodayKwh { get; set; }
    public ushort? HeatingHeatYesterdayKwh { get; set; }
    public ushort? HeatingHeatMonthKwh { get; set; }
    public ushort? HeatingHeatYearKwh { get; set; }
    public ushort? HotWaterHeatTodayKwh { get; set; }
    public ushort? HotWaterHeatYesterdayKwh { get; set; }
    public ushort? HotWaterHeatMonthKwh { get; set; }
    public ushort? HotWaterHeatYearKwh { get; set; }
    public ushort? ElectricalTodayKwh { get; set; }
    public ushort? ElectricalYesterdayKwh { get; set; }
    public ushort? ElectricalMonthKwh { get; set; }
    public ushort? ElectricalYearKwh { get; set; }
}
