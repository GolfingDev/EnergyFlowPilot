namespace TibberVictronController.Business.Models;

/// <summary>
/// One strictly read-only Modbus telemetry snapshot of a Weishaupt Biblock heat pump.
/// </summary>
public sealed class WeishauptBiblockTelemetrySample
{
    private readonly DateTimeOffset measuredAtUtc;

    public required DateTimeOffset MeasuredAtUtc
    {
        get => measuredAtUtc;
        init
        {
            if (value.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("Der Messzeitpunkt der Weishaupt-Telemetrie muss in UTC angegeben sein.", nameof(MeasuredAtUtc));
            }

            measuredAtUtc = value;
        }
    }

    public required decimal OutdoorTemperatureCelsius { get; init; }

    /// <summary>Raw value from input register 33101; no control value is derived from it.</summary>
    public required ushort OperatingStatusCode { get; init; }

    public required decimal HotWaterTemperatureCelsius { get; init; }

    // Unsigned whole-kWh device counters; null means unavailable, never zero consumption.
    public ushort? TotalHeatTodayKwh { get; init; }
    public ushort? TotalHeatYesterdayKwh { get; init; }
    public ushort? TotalHeatMonthKwh { get; init; }
    public ushort? TotalHeatYearKwh { get; init; }
    public ushort? HeatingHeatTodayKwh { get; init; }
    public ushort? HeatingHeatYesterdayKwh { get; init; }
    public ushort? HeatingHeatMonthKwh { get; init; }
    public ushort? HeatingHeatYearKwh { get; init; }
    public ushort? HotWaterHeatTodayKwh { get; init; }
    public ushort? HotWaterHeatYesterdayKwh { get; init; }
    public ushort? HotWaterHeatMonthKwh { get; init; }
    public ushort? HotWaterHeatYearKwh { get; init; }
    public ushort? ElectricalTodayKwh { get; init; }
    public ushort? ElectricalYesterdayKwh { get; init; }
    public ushort? ElectricalMonthKwh { get; init; }
    public ushort? ElectricalYearKwh { get; init; }
}
