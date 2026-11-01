using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// PV forecast correction from the controller's own measurements: for each local hour of the day, measured PV divided
/// by the last forecast made before that quarter hour, over the last two weeks. Systematic errors of the forecast
/// service (e.g. morning shade, a wrong azimuth) show up as a factor away from 1.
/// </summary>
public static class PvForecastCorrectionLearner
{
    public const int RequiredDays = 7;
    private const double MinimumFactor = 0.5;
    private const double MaximumFactor = 1.5;
    // Hours with less forecast energy than this over the whole window say too little about a systematic error.
    private const double MinimumForecastKwhPerHour = 0.5;
    private const int MinimumSamplesPerQuarterHour = 3;

    public static PvForecastCorrection Learn(IReadOnlyList<PvForecastSnapshot> forecasts, IReadOnlyList<LiveConsumptionSample> samples,
        TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        // Measured PV per quarter hour (E3/DC PV meter, the site's PV), only with enough samples.
        var measured = samples
            .Where(sample => sample.SupplementaryPvProductionWatts is not null)
            .GroupBy(sample => QuarterStart(sample.MeasuredAtUtc))
            .Where(group => group.Count() >= MinimumSamplesPerQuarterHour)
            .ToDictionary(group => group.Key, group => (double)group.Average(sample => sample.SupplementaryPvProductionWatts!.Value) / 1000d * 0.25);
        var pairs = forecasts
            .Where(forecast => forecast.SlotStartUtc.AddMinutes(15) <= nowUtc && measured.ContainsKey(forecast.SlotStartUtc))
            .Select(forecast => (Local: TimeZoneInfo.ConvertTime(forecast.SlotStartUtc, zone), Forecast: (double)forecast.ForecastKwh,
                Measured: measured[forecast.SlotStartUtc]))
            .Where(pair => pair.Forecast > 0.01 || pair.Measured > 0.01)
            .ToArray();
        var days = pairs.Where(pair => pair.Forecast > 0.05).Select(pair => DateOnly.FromDateTime(pair.Local.DateTime)).Distinct().Count();
        // Few days: factors are pulled towards 1, so one cloudy day cannot halve the next plan.
        var weight = Math.Min(1, days / (double)RequiredDays);
        double Shrink(double sumMeasured, double sumForecast) =>
            Math.Clamp(1 + (sumMeasured / sumForecast - 1) * weight, MinimumFactor, MaximumFactor);

        var byHour = pairs.GroupBy(pair => pair.Local.Hour)
            .Where(group => group.Sum(pair => pair.Forecast) >= MinimumForecastKwhPerHour)
            .ToDictionary(group => group.Key, group => Math.Round(Shrink(group.Sum(pair => pair.Measured), group.Sum(pair => pair.Forecast)), 3));
        var totalForecast = pairs.Sum(pair => pair.Forecast);
        var overall = totalForecast >= MinimumForecastKwhPerHour ? Math.Round(Shrink(pairs.Sum(pair => pair.Measured), totalForecast), 3) : 1;
        return new PvForecastCorrection(nowUtc, byHour, overall, days, days >= RequiredDays);
    }

    private static DateTimeOffset QuarterStart(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % TimeSpan.FromMinutes(15).Ticks, TimeSpan.Zero);
}

/// <summary>
/// Car consumption from its own drives: between two CarData samples the odometer rose and the state of charge fell
/// (no charging in between). Each drive is normalised to mild weather with the winter surcharge, so the trip
/// planner can add the surcharge for the forecast temperature without counting winter twice. The median resists
/// odd drives (short trips with heating, motorway).
/// </summary>
public static class CarConsumptionLearner
{
    public const int RequiredDrives = 3;
    private const double MinimumDriveKm = 10;
    private const double MaximumDriveKm = 1000;
    private const double MinimumTotalKm = 60;
    private const int UsedDrives = 20;

    public static LearnedConsumption? Learn(IReadOnlyList<CarSample> samples, double capacityKwh,
        IReadOnlyList<HourlyTemperature>? temperatures, double winterSurchargePerKelvinPercent)
    {
        var ordered = samples.Where(sample => sample.SocPercent is not null && sample.OdometerKm is not null)
            .OrderBy(sample => sample.MeasuredAtUtc).ToArray();
        var drives = new List<(DateTimeOffset At, double KwhPer100Km, double Km)>();
        foreach (var (first, last) in DriveSegments(ordered))
        {
            var km = last.OdometerKm!.Value - first.OdometerKm!.Value;
            var socDrop = first.SocPercent!.Value - last.SocPercent!.Value;
            if (km is < MinimumDriveKm or > MaximumDriveKm || socDrop <= 0) continue;
            var consumption = socDrop / 100d * capacityKwh / km * 100;
            var surcharge = WinterSurchargePercent(temperatures, first.MeasuredAtUtc, last.MeasuredAtUtc, winterSurchargePerKelvinPercent);
            var normalised = consumption / (1 + surcharge / 100d);
            if (normalised is >= 8 and <= 50) drives.Add((last.MeasuredAtUtc, normalised, km));
        }

        var recent = drives.OrderByDescending(drive => drive.At).Take(UsedDrives).ToArray();
        if (recent.Length < RequiredDrives || recent.Sum(drive => drive.Km) < MinimumTotalKm) return null;
        var sorted = recent.Select(drive => drive.KwhPer100Km).Order().ToArray();
        var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        return new LearnedConsumption(Math.Round(median, 1), recent.Length, Math.Round(recent.Sum(drive => drive.Km)));
    }

    // While driving the car reports often; consecutive reports belong to one drive until it charges (state of charge
    // rises) or nothing comes for two hours (parked: standby losses must not count as driving consumption).
    private static IEnumerable<(CarSample First, CarSample Last)> DriveSegments(CarSample[] ordered)
    {
        if (ordered.Length == 0) yield break;
        var first = ordered[0];
        var last = ordered[0];
        foreach (var sample in ordered.Skip(1))
        {
            var charging = sample.SocPercent!.Value > last.SocPercent!.Value + 0.5;
            var parked = sample.MeasuredAtUtc - last.MeasuredAtUtc > TimeSpan.FromHours(2);
            if (charging || parked)
            {
                yield return (first, last);
                first = sample;
            }

            last = sample;
        }

        yield return (first, last);
    }

    private static double WinterSurchargePercent(IReadOnlyList<HourlyTemperature>? temperatures, DateTimeOffset from, DateTimeOffset to, double perKelvin)
    {
        if (temperatures is null || perKelvin <= 0) return 0;
        var inside = temperatures.Where(hour => hour.StartsAtUtc >= from.AddHours(-1) && hour.StartsAtUtc <= to).ToArray();
        if (inside.Length == 0) return 0;
        return Math.Clamp((15 - (double)inside.Average(hour => hour.TemperatureCelsius)) * perKelvin, 0, 40);
    }
}
