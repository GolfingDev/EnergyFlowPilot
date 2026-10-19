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
}
