using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Weather;

/// <summary>
/// Loads weather over Germany from Open-Meteo and German day-ahead prices from Energy-Charts (Fraunhofer ISE).
/// </summary>
/// <remarks>
/// Both APIs are free and need no key. Wind is taken from northern locations where most German wind capacity stands,
/// irradiance from southern and central locations with most PV. Open-Meteo returns several locations in one request, so
/// one load needs three requests; the result is cached for one hour because Energy-Charts answers bursts with HTTP 429.
/// </remarks>
public sealed class PriceDriverProvider : IPriceDriverProvider
{
    private const string OpenMeteoEndpoint = "https://api.open-meteo.com/v1/forecast";
    private const string EnergyChartsPriceEndpoint = "https://api.energy-charts.info/price";
    private const int HistoryDays = 42;
    private const int ForecastDays = 4;

    private static readonly (decimal Latitude, decimal Longitude)[] WindLocations =
    {
        (54.5m, 9.0m), (53.4m, 7.2m), (54.1m, 12.1m), (52.5m, 11.5m), (53.0m, 9.5m)
    };

    private static readonly (decimal Latitude, decimal Longitude)[] SolarLocations =
    {
        (48.1m, 11.6m), (48.8m, 9.2m), (49.5m, 11.1m), (50.5m, 9.7m), (51.3m, 12.4m)
    };

    private readonly HttpClient httpClient;
    private readonly PriceDriverCache cache;
    private readonly ILogger<PriceDriverProvider> logger;

    public PriceDriverProvider(HttpClient httpClient, PriceDriverCache cache, ILogger<PriceDriverProvider> logger)
    {
        this.httpClient = httpClient;
        this.cache = cache;
        this.logger = logger;
    }

    public Task<PriceDriverData?> GetPriceDriversAsync(CancellationToken cancellationToken = default)
    {
        return cache.GetOrCreateAsync(LoadAsync, cancellationToken);
    }

    private async Task<PriceDriverData?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var windSpeeds = await LoadOpenMeteoAsync(WindLocations, "wind_speed_100m", cancellationToken);
            var irradiance = await LoadOpenMeteoAsync(SolarLocations, "shortwave_radiation", cancellationToken);
            var spotPrices = await LoadSpotPricesAsync(cancellationToken);

            var weather = windSpeeds.Keys
                .Where(irradiance.ContainsKey)
                .OrderBy(hour => hour)
                .Select(hour => new PriceDriverHour(
                    hour,
                    windSpeeds[hour].Average(CalculateWindIndex),
                    irradiance[hour].Average() / 1000m))
                .ToArray();

            logger.LogInformation(
                "Preistreiber geladen: {WeatherHours} Wetterstunden, {SpotSlots} Spotpreise.",
                weather.Length,
                spotPrices.Count);

            return new PriceDriverData(weather, spotPrices, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Wetter- oder Spotpreisdaten fuer die Preisschaetzung konnten nicht geladen werden.");
            return null;
        }
    }

    /// <summary>
    /// Simple turbine curve: no output below 3 m/s, rated output from 12 m/s, cut-out at 25 m/s.
    /// </summary>
    public static decimal CalculateWindIndex(decimal windSpeedMetersPerSecond)
    {
        if (windSpeedMetersPerSecond >= 25m)
        {
            return 0m;
        }

        var relative = Math.Clamp((windSpeedMetersPerSecond - 3m) / 9m, 0m, 1m);
        return relative * relative * relative;
    }

    private async Task<Dictionary<DateTimeOffset, List<decimal>>> LoadOpenMeteoAsync(
        IReadOnlyList<(decimal Latitude, decimal Longitude)> locations,
        string variable,
        CancellationToken cancellationToken)
    {
        var latitudes = string.Join(",", locations.Select(location => location.Latitude.ToString(CultureInfo.InvariantCulture)));
        var longitudes = string.Join(",", locations.Select(location => location.Longitude.ToString(CultureInfo.InvariantCulture)));
        var requestUri = $"{OpenMeteoEndpoint}?latitude={latitudes}&longitude={longitudes}&hourly={variable}&past_days={HistoryDays}&forecast_days={ForecastDays}&timezone=GMT&wind_speed_unit=ms";

        using var document = await GetJsonAsync(requestUri, cancellationToken);
        var locationElements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : new[] { document.RootElement };
        var valuesByHour = new Dictionary<DateTimeOffset, List<decimal>>();

        foreach (var location in locationElements)
        {
            var hourly = location.GetProperty("hourly");
            var times = hourly.GetProperty("time").EnumerateArray().ToArray();
            var values = hourly.GetProperty(variable).EnumerateArray().ToArray();

            for (var index = 0; index < Math.Min(times.Length, values.Length); index++)
            {
                if (values[index].ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var hour = new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(times[index].GetString()!, CultureInfo.InvariantCulture), DateTimeKind.Utc));
                if (!valuesByHour.TryGetValue(hour, out var list))
                {
                    list = new List<decimal>();
                    valuesByHour[hour] = list;
                }

                list.Add(values[index].GetDecimal());
            }
        }

        // Hours with missing locations would mix different station sets; only complete hours are used.
        return valuesByHour
            .Where(pair => pair.Value.Count == locations.Count)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private async Task<IReadOnlyList<SpotPriceSlot>> LoadSpotPricesAsync(CancellationToken cancellationToken)
    {
        var endDate = DateTime.UtcNow.Date.AddDays(2);
        var startDate = endDate.AddDays(-HistoryDays - 2);
        var requestUri = $"{EnergyChartsPriceEndpoint}?bzn=DE-LU&start={startDate:yyyy-MM-dd}&end={endDate:yyyy-MM-dd}";

        using var document = await GetJsonAsync(requestUri, cancellationToken);
        var times = document.RootElement.GetProperty("unix_seconds").EnumerateArray().ToArray();
        var prices = document.RootElement.GetProperty("price").EnumerateArray().ToArray();
        var spotPrices = new List<SpotPriceSlot>();

        for (var index = 0; index < Math.Min(times.Length, prices.Length); index++)
        {
            if (prices[index].ValueKind == JsonValueKind.Number)
            {
                spotPrices.Add(new SpotPriceSlot(DateTimeOffset.FromUnixTimeSeconds(times[index].GetInt64()), prices[index].GetDecimal()));
            }
        }

        return spotPrices;
    }

    private async Task<JsonDocument> GetJsonAsync(string requestUri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Die Anfrage an {new Uri(requestUri).Host} ist mit Status {(int)response.StatusCode} fehlgeschlagen.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Keeps the last price driver load for one hour, including failed loads, to respect the public APIs.
/// </summary>
public sealed class PriceDriverCache
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim cacheLock = new(1, 1);
    private PriceDriverData? data;
    private DateTimeOffset? loadedAtUtc;

    public async Task<PriceDriverData?> GetOrCreateAsync(
        Func<CancellationToken, Task<PriceDriverData?>> loadAsync,
        CancellationToken cancellationToken)
    {
        await cacheLock.WaitAsync(cancellationToken);

        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            if (loadedAtUtc is null || nowUtc - loadedAtUtc.Value >= CacheDuration)
            {
                data = await loadAsync(cancellationToken);
                loadedAtUtc = nowUtc;
            }

            return data;
        }
        finally
        {
            cacheLock.Release();
        }
    }
}
