using Microsoft.Extensions.Options;

namespace TibberVictronController.Web.Services;

public interface IPvForecastService
{
    Task<double> GetExpectedPvWhAsync(DateTimeOffset targetTime, CancellationToken cancellationToken = default);
}

public class PvForecastService : IPvForecastService
{
    private readonly ForecastOptions _forecastOptions;
    private readonly IWeatherForecastService _weatherForecastService;
    private readonly ILogger<PvForecastService> _logger;

    public PvForecastService(
        IOptions<ForecastOptions> forecastOptions,
        IWeatherForecastService weatherForecastService,
        ILogger<PvForecastService> logger)
    {
        _forecastOptions = forecastOptions.Value;
        _weatherForecastService = weatherForecastService;
        _logger = logger;
    }

    public async Task<double> GetExpectedPvWhAsync(DateTimeOffset targetTime, CancellationToken cancellationToken = default)
    {
        var weatherValue = await _weatherForecastService.GetExpectedPvWattsAsync(targetTime, cancellationToken);
        if (weatherValue.HasValue)
        {
            return weatherValue.Value;
        }

        _logger.LogDebug("Falling back to configured/default PV forecast for {TargetTime}", targetTime);

        var hour = targetTime.UtcDateTime.Hour;
        var configured = _forecastOptions.PvBlocks.FirstOrDefault(x => hour >= x.StartHour && hour < x.EndHour)?.PvWatts;

        if (configured.HasValue)
        {
            return configured.Value * _forecastOptions.DefaultPvScaling;
        }

        var seasonalFactor = GetSeasonalFactor(targetTime.Month);
        var defaultCurve = GetDefaultPvCurve(targetTime.UtcDateTime.Hour, targetTime.UtcDateTime.Minute) * seasonalFactor * _forecastOptions.DefaultPvScaling;
        return defaultCurve;
    }

    private static double GetSeasonalFactor(int month)
    {
        return month switch
        {
            11 or 12 or 1 => 0.25,
            2 or 3 => 0.45,
            4 or 5 => 0.75,
            6 or 7 or 8 => 1.0,
            9 or 10 => 0.55,
            _ => 0.6
        };
    }

    private static double GetDefaultPvCurve(int hour, int minute)
    {
        var anchors = new Dictionary<int, double>
        {
            [0] = 0,
            [6] = 120,
            [7] = 260,
            [8] = 450,
            [9] = 750,
            [10] = 1100,
            [11] = 1500,
            [12] = 1800,
            [13] = 1700,
            [14] = 1450,
            [15] = 1100,
            [16] = 700,
            [17] = 350,
            [18] = 120,
            [24] = 0
        };

        if (hour < 6 || hour >= 18)
        {
            return 0;
        }

        var current = anchors[hour];
        var nextHour = Math.Min(24, hour + 1);
        var next = anchors.TryGetValue(nextHour, out var nextValue) ? nextValue : current;
        var ratio = Math.Clamp(minute / 60.0, 0, 1);
        return current + ((next - current) * ratio);
    }
}
