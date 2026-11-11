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

    /// <summary>
    /// Compressor power demand in percent (input register 33103). Source: community register list, not the
    /// manufacturer document; not yet checked on the installed device. It is no electrical power measurement.
    /// Null when unavailable or outside 0–100 %.
    /// </summary>
    public ushort? PowerDemandPercent { get; init; }

    // Flow temperatures in °C (signed tenths on the wire), read so the heating curve and COP can be judged
    // against flow temperature. Source: community register list (hpconst.py); the circuit numbers match this
    // installation (circuit 1 direct radiators, circuit 3 mixed underfloor). Null means unavailable or implausible.

    /// <summary>Flow set point requested by heating circuit 1 (input register 31104).</summary>
    public decimal? HeatingCircuit1FlowSetPointCelsius { get; init; }

    /// <summary>Flow set point of heating circuit 3 (input register 31304).</summary>
    public decimal? HeatingCircuit3FlowSetPointCelsius { get; init; }

    /// <summary>Measured flow temperature behind the mixer of heating circuit 3 (input register 31305).</summary>
    public decimal? HeatingCircuit3FlowTemperatureCelsius { get; init; }

    /// <summary>Heat pump flow temperature (input register 33104).</summary>
    public decimal? HeatPumpFlowTemperatureCelsius { get; init; }

    /// <summary>Heat pump return temperature (input register 33105).</summary>
    public decimal? HeatPumpReturnTemperatureCelsius { get; init; }
}
