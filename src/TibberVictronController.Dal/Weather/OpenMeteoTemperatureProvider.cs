using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Weather;

/// <summary>
/// Loads measured and forecast hourly outdoor temperatures from the free Open-Meteo API for the configured PV site.
/// </summary>
/// <remarks>
/// Temperatures only refine the consumption forecast. A failing request therefore returns an empty list and the forecast
/// falls back to the plain learned profile instead of breaking the battery plan.
/// </remarks>
public sealed class OpenMeteoTemperatureProvider : ITemperatureProvider
{
    private const string ApiEndpoint = "https://api.open-meteo.com/v1/forecast";
    private const int PastDays = 16;
    private const int ForecastDays = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly IControllerSettingStore controllerSettingStore;
    private readonly OpenMeteoTemperatureCache cache;
    private readonly ILogger<OpenMeteoTemperatureProvider> logger;

    public OpenMeteoTemperatureProvider(
        HttpClient httpClient,
        IControllerSettingStore controllerSettingStore,
        OpenMeteoTemperatureCache cache,
        ILogger<OpenMeteoTemperatureProvider> logger)
    {
        this.httpClient = httpClient;
        this.controllerSettingStore = controllerSettingStore;
        this.cache = cache;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<HourlyTemperature>> GetHourlyTemperaturesAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        // One fixed request window (past 16 days plus 4 forecast days) covers the profile history and the planning horizon,
        // so the API is called at most once per hour no matter how often the forecast is recalculated.
        var temperatures = await cache.GetOrCreateAsync(LoadAsync, cancellationToken);

        return temperatures
            .Where(temperature => temperature.StartsAtUtc >= startsAtUtc && temperature.StartsAtUtc < endsAtUtc)
            .ToArray();
    }

    private async Task<IReadOnlyList<HourlyTemperature>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var latitude = await GetDecimalSettingAsync(ControllerSettingDefaults.PvForecastLatitudeKey, cancellationToken);
            var longitude = await GetDecimalSettingAsync(ControllerSettingDefaults.PvForecastLongitudeKey, cancellationToken);

            if (latitude is null || longitude is null)
            {
                logger.LogWarning("Aussentemperaturen werden nicht geladen, weil der PV-Standort nicht konfiguriert ist.");
                return Array.Empty<HourlyTemperature>();
            }

            var requestUri = string.Create(
                CultureInfo.InvariantCulture,
                $"{ApiEndpoint}?latitude={latitude}&longitude={longitude}&hourly=temperature_2m&past_days={PastDays}&forecast_days={ForecastDays}&timezone=GMT");
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Open-Meteo hat die Aussentemperaturen mit Status {StatusCode} abgelehnt.", (int)response.StatusCode);
                return Array.Empty<HourlyTemperature>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<OpenMeteoResponse>(stream, JsonOptions, cancellationToken);

            return MapTemperatures(payload);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Aussentemperaturen konnten nicht von Open-Meteo geladen werden.");
            return Array.Empty<HourlyTemperature>();
        }
    }

    /// <summary>
    /// Maps the Open-Meteo hourly arrays (requested in GMT) to UTC hours and skips hours without a value.
    /// </summary>
    public static IReadOnlyList<HourlyTemperature> MapTemperatures(OpenMeteoResponse? payload)
    {
        var times = payload?.Hourly?.Time;
        var values = payload?.Hourly?.Temperature2m;

        if (times is null || values is null)
        {
            return Array.Empty<HourlyTemperature>();
        }

        var temperatures = new List<HourlyTemperature>();

        for (var index = 0; index < Math.Min(times.Count, values.Count); index++)
        {
            if (values[index] is not { } value ||
                !DateTime.TryParse(times[index], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startsAt))
            {
                continue;
            }

            temperatures.Add(new HourlyTemperature(new DateTimeOffset(startsAt, TimeSpan.Zero), value));
        }

        return temperatures;
    }

    private async Task<decimal?> GetDecimalSettingAsync(string settingKey, CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(settingKey, cancellationToken);

        return setting is { IsConfigured: true } &&
               decimal.TryParse(setting.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public sealed class OpenMeteoResponse
    {
        public OpenMeteoHourly? Hourly { get; init; }
    }

    public sealed class OpenMeteoHourly
    {
        public IReadOnlyList<string>? Time { get; init; }

        [JsonPropertyName("temperature_2m")]
        public IReadOnlyList<decimal?>? Temperature2m { get; init; }
    }
}

/// <summary>
/// Keeps the last Open-Meteo result for one hour, including empty results after failures, to respect the public API.
/// </summary>
public sealed class OpenMeteoTemperatureCache
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim cacheLock = new(1, 1);
    private IReadOnlyList<HourlyTemperature>? temperatures;
    private DateTimeOffset loadedAtUtc;

    public async Task<IReadOnlyList<HourlyTemperature>> GetOrCreateAsync(
        Func<CancellationToken, Task<IReadOnlyList<HourlyTemperature>>> loadAsync,
        CancellationToken cancellationToken)
    {
        await cacheLock.WaitAsync(cancellationToken);

        try
        {
            var nowUtc = DateTimeOffset.UtcNow;

            if (temperatures is null || nowUtc - loadedAtUtc >= CacheDuration)
            {
                temperatures = await loadAsync(cancellationToken);
                loadedAtUtc = nowUtc;
            }

            return temperatures;
        }
        finally
        {
            cacheLock.Release();
        }
    }
}
