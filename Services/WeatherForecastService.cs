using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace TibberVictronController.Web.Services;

public record WeatherPvForecastPoint(DateTimeOffset StartsAt, double GlobalTiltedIrradiance, double ExpectedPvWatts);

public interface IWeatherForecastService
{
    Task<IReadOnlyList<WeatherPvForecastPoint>> GetPvForecastAsync(CancellationToken cancellationToken = default);
    Task<double?> GetExpectedPvWattsAsync(DateTimeOffset targetTime, CancellationToken cancellationToken = default);
}

public class WeatherForecastService : IWeatherForecastService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WeatherForecastOptions _options;
    private readonly ILogger<WeatherForecastService> _logger;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private List<WeatherPvForecastPoint> _cachedPoints = new();
    private DateTime _cacheFetchedUtc = DateTime.MinValue;

    public WeatherForecastService(
        IHttpClientFactory httpClientFactory,
        IOptions<WeatherForecastOptions> options,
        ILogger<WeatherForecastService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WeatherPvForecastPoint>> GetPvForecastAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Array.Empty<WeatherPvForecastPoint>();
        }

        if (IsCacheFresh())
        {
            return _cachedPoints;
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (IsCacheFresh())
            {
                return _cachedPoints;
            }

            var client = _httpClientFactory.CreateClient(nameof(WeatherForecastService));
            client.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.RequestTimeoutSeconds));

            var requestUri = BuildRequestUri();
            _logger.LogInformation("Requesting weather PV forecast from {Uri}", requestUri);

            using var response = await client.GetAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<OpenMeteoForecastResponse>(cancellationToken: cancellationToken);
            var mapped = MapResponse(payload);

            _cachedPoints = mapped.OrderBy(x => x.StartsAt).ToList();
            _cacheFetchedUtc = DateTime.UtcNow;

            return _cachedPoints;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Weather forecast request failed. Using cached data if available.");
            return _cachedPoints;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<double?> GetExpectedPvWattsAsync(DateTimeOffset targetTime, CancellationToken cancellationToken = default)
    {
        var forecast = await GetPvForecastAsync(cancellationToken);
        if (forecast.Count == 0)
        {
            return null;
        }

        var targetUtc = targetTime.ToUniversalTime();
        var exact = forecast.FirstOrDefault(x => x.StartsAt == targetUtc);
        if (exact is not null)
        {
            return exact.ExpectedPvWatts;
        }

        var ordered = forecast.OrderBy(x => x.StartsAt).ToList();
        var before = ordered.LastOrDefault(x => x.StartsAt <= targetUtc);
        var after = ordered.FirstOrDefault(x => x.StartsAt >= targetUtc);

        if (before is null && after is null)
        {
            return null;
        }

        if (before is null)
        {
            return Math.Abs((after!.StartsAt - targetUtc).TotalHours) <= 1.5 ? after.ExpectedPvWatts : null;
        }

        if (after is null)
        {
            return Math.Abs((before.StartsAt - targetUtc).TotalHours) <= 1.5 ? before.ExpectedPvWatts : null;
        }

        var totalMinutes = (after.StartsAt - before.StartsAt).TotalMinutes;
        if (totalMinutes <= 0)
        {
            return before.ExpectedPvWatts;
        }

        if (Math.Abs((after.StartsAt - targetUtc).TotalHours) > 1.5 && Math.Abs((before.StartsAt - targetUtc).TotalHours) > 1.5)
        {
            return null;
        }

        var ratio = Math.Clamp((targetUtc - before.StartsAt).TotalMinutes / totalMinutes, 0, 1);
        return before.ExpectedPvWatts + ((after.ExpectedPvWatts - before.ExpectedPvWatts) * ratio);
    }

    private bool IsCacheFresh()
        => _cachedPoints.Count > 0 && (DateTime.UtcNow - _cacheFetchedUtc) < TimeSpan.FromMinutes(Math.Max(1, _options.CacheMinutes));

    private string BuildRequestUri()
    {
        var query = new Dictionary<string, string>
        {
            ["latitude"] = _options.Latitude.ToString(CultureInfo.InvariantCulture),
            ["longitude"] = _options.Longitude.ToString(CultureInfo.InvariantCulture),
            ["hourly"] = "global_tilted_irradiance",
            ["forecast_hours"] = Math.Max(24, _options.ForecastHours).ToString(CultureInfo.InvariantCulture),
            ["tilt"] = _options.PanelTiltDegrees.ToString(CultureInfo.InvariantCulture),
            ["azimuth"] = _options.PanelAzimuthDegrees.ToString(CultureInfo.InvariantCulture),
            ["timezone"] = string.IsNullOrWhiteSpace(_options.Timezone) ? "UTC" : _options.Timezone
        };

        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            query["models"] = _options.Model;
        }

        var qs = string.Join("&", query.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        return _options.BaseUrl.TrimEnd('/') + "/v1/forecast?" + qs;
    }

    private List<WeatherPvForecastPoint> MapResponse(OpenMeteoForecastResponse? payload)
    {
        if (payload?.Hourly?.Time is null || payload.Hourly.GlobalTiltedIrradiance is null)
        {
            return new List<WeatherPvForecastPoint>();
        }

        var count = Math.Min(payload.Hourly.Time.Count, payload.Hourly.GlobalTiltedIrradiance.Count);
        var result = new List<WeatherPvForecastPoint>(count);

        for (var i = 0; i < count; i++)
        {
            if (!DateTimeOffset.TryParse(payload.Hourly.Time[i], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var startsAt))
            {
                continue;
            }

            var gti = Math.Max(0, payload.Hourly.GlobalTiltedIrradiance[i]);
            var pvWatts = ConvertIrradianceToPvWatts(gti);
            result.Add(new WeatherPvForecastPoint(startsAt.ToUniversalTime(), gti, pvWatts));
        }

        return result;
    }

    private double ConvertIrradianceToPvWatts(double globalTiltedIrradiance)
    {
        if (globalTiltedIrradiance < _options.MinimumUsefulIrradianceWattsPerSquareMeter)
        {
            return 0;
        }

        var dcEstimateWatts = _options.PvSystemKwPeak * globalTiltedIrradiance * _options.PerformanceRatio * _options.AdditionalLossFactor;
        return Math.Max(0, Math.Min(_options.MaxAcPowerWatts, dcEstimateWatts));
    }

    private sealed class OpenMeteoForecastResponse
    {
        public OpenMeteoHourly? Hourly { get; set; }
    }

    private sealed class OpenMeteoHourly
    {
        public List<string> Time { get; set; } = new();
        public List<double> GlobalTiltedIrradiance { get; set; } = new();
    }
}
