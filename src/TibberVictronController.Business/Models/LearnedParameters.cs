namespace TibberVictronController.Business.Models;

/// <summary>Last PV forecast made for one quarter hour before it started.</summary>
public sealed record PvForecastSnapshot(DateTimeOffset SlotStartUtc, decimal ForecastKwh);

/// <summary>
/// Learned ratio of measured to forecast PV per local hour of day (and overall). Ready only with enough days of data;
/// factors are pulled towards 1 while data is thin and stay within 0.5 to 1.5.
/// </summary>
public sealed record PvForecastCorrection(
    DateTimeOffset CalculatedAtUtc,
    IReadOnlyDictionary<int, double> FactorByLocalHour,
    double OverallFactor,
    int DaysWithData,
    bool IsReady)
{
    public static readonly PvForecastCorrection None = new(DateTimeOffset.MinValue, new Dictionary<int, double>(), 1, 0, false);

    public double FactorFor(DateTimeOffset slotStartUtc, TimeZoneInfo zone) =>
        FactorByLocalHour.TryGetValue(TimeZoneInfo.ConvertTime(slotStartUtc, zone).Hour, out var factor) ? factor : OverallFactor;
}

/// <summary>State of charge and odometer of the car at one CarData message.</summary>
public sealed record CarSample(DateTimeOffset MeasuredAtUtc, double? SocPercent, double? OdometerKm);

/// <summary>Consumption learned from the car's own drives, normalised to mild weather (no winter surcharge included).</summary>
public sealed record LearnedConsumption(double KwhPer100Km, int Drives, double TotalKm);
