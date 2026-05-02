using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Weather;

/// <summary>
/// Loads PV yield estimates from the Forecast.Solar public API and maps them to Decision Engine forecast slots.
/// </summary>
public sealed class ForecastSolarPvForecastProvider : IWeatherForecastProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly IControllerSettingStore controllerSettingStore;

    public ForecastSolarPvForecastProvider(
        HttpClient httpClient,
        IControllerSettingStore controllerSettingStore)
    {
        this.httpClient = httpClient;
        this.controllerSettingStore = controllerSettingStore;
    }

    /// <summary>
    /// Loads Forecast.Solar data and converts the hourly public forecast into 15-minute PV yield slots.
    /// </summary>
    public async Task<IReadOnlyList<PvYieldForecastSlot>> GetPvYieldForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateUtcRange(startsAtUtc, endsAtUtc);

        var configuration = await LoadConfigurationAsync(cancellationToken);
        var requestUri = BuildRequestUri(configuration);
        var response = await ExecuteRequestAsync(requestUri, cancellationToken);

        return MapToForecastSlots(response, configuration.TimeZone, startsAtUtc, endsAtUtc);
    }

    private async Task<ForecastSolarConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        var endpoint = await GetRequiredSettingValueAsync(
            ControllerSettingDefaults.PvForecastApiEndpointKey,
            "Der Forecast.Solar API-Endpunkt ist nicht konfiguriert.",
            cancellationToken);
        var apiKey = await GetOptionalSettingValueAsync(
            ControllerSettingDefaults.PvForecastApiKeyKey,
            cancellationToken);
        var latitude = await GetRequiredDecimalSettingAsync(
            ControllerSettingDefaults.PvForecastLatitudeKey,
            "Die PV-Standort-Breitengrad-Einstellung ist nicht konfiguriert.",
            "Die PV-Standort-Breitengrad-Einstellung muss als Dezimalzahl konfiguriert sein.",
            cancellationToken);
        var longitude = await GetRequiredDecimalSettingAsync(
            ControllerSettingDefaults.PvForecastLongitudeKey,
            "Die PV-Standort-Laengengrad-Einstellung ist nicht konfiguriert.",
            "Die PV-Standort-Laengengrad-Einstellung muss als Dezimalzahl konfiguriert sein.",
            cancellationToken);
        var peakPowerKwp = await GetRequiredDecimalSettingAsync(
            ControllerSettingDefaults.PvForecastPeakPowerKwpKey,
            "Die PV-Anlagenleistung ist nicht konfiguriert.",
            "Die PV-Anlagenleistung muss als Dezimalzahl konfiguriert sein.",
            cancellationToken);
        var declinationDegrees = await GetRequiredDecimalSettingAsync(
            ControllerSettingDefaults.PvForecastDeclinationDegreesKey,
            "Die PV-Modulneigung ist nicht konfiguriert.",
            "Die PV-Modulneigung muss als Dezimalzahl konfiguriert sein.",
            cancellationToken);
        var azimuthDegrees = await GetRequiredDecimalSettingAsync(
            ControllerSettingDefaults.PvForecastAzimuthDegreesKey,
            "Die PV-Modulausrichtung ist nicht konfiguriert.",
            "Die PV-Modulausrichtung muss als Dezimalzahl konfiguriert sein.",
            cancellationToken);
        var timeZoneId = await GetRequiredSettingValueAsync(
            ControllerSettingDefaults.PvForecastTimeZoneKey,
            "Die PV-Forecast-Zeitzone ist nicht konfiguriert.",
            cancellationToken);

        if (peakPowerKwp <= 0m)
        {
            throw new InvalidOperationException("Die PV-Anlagenleistung muss groesser als 0 kWp sein.");
        }

        return new ForecastSolarConfiguration
        {
            ApiEndpoint = endpoint,
            ApiKey = apiKey,
            Latitude = latitude,
            Longitude = longitude,
            DeclinationDegrees = declinationDegrees,
            AzimuthDegrees = azimuthDegrees,
            PeakPowerKwp = peakPowerKwp,
            TimeZone = ResolveTimeZone(timeZoneId)
        };
    }

    private async Task<string> GetRequiredSettingValueAsync(
        string settingKey,
        string missingMessage,
        CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(settingKey, cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            throw new InvalidOperationException(missingMessage);
        }

        return setting.Value!;
    }

    private async Task<string?> GetOptionalSettingValueAsync(
        string settingKey,
        CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(settingKey, cancellationToken);

        return setting is null || !setting.IsConfigured
            ? null
            : setting.Value;
    }

    private async Task<decimal> GetRequiredDecimalSettingAsync(
        string settingKey,
        string missingMessage,
        string invalidMessage,
        CancellationToken cancellationToken)
    {
        var settingValue = await GetRequiredSettingValueAsync(settingKey, missingMessage, cancellationToken);

        if (!decimal.TryParse(settingValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException(invalidMessage);
        }

        return value;
    }

    private static Uri BuildRequestUri(ForecastSolarConfiguration configuration)
    {
        var endpoint = AddApiKeyToEndpointIfConfigured(
            configuration.ApiEndpoint.TrimEnd('/'),
            configuration.ApiKey);

        if (!Uri.TryCreate(endpoint + "/", UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException("Der Forecast.Solar API-Endpunkt ist keine gueltige absolute URL.");
        }

        var path = string.Join(
            "/",
            FormatDecimal(configuration.Latitude),
            FormatDecimal(configuration.Longitude),
            FormatDecimal(configuration.DeclinationDegrees),
            FormatDecimal(NormalizeAzimuthForForecastSolar(configuration.AzimuthDegrees)),
            FormatDecimal(configuration.PeakPowerKwp));

        return new Uri(endpointUri, path);
    }

    private static decimal NormalizeAzimuthForForecastSolar(decimal azimuthDegrees)
    {
        var normalizedAzimuth = azimuthDegrees % 360m;

        if (normalizedAzimuth > 180m)
        {
            return normalizedAzimuth - 360m;
        }

        if (normalizedAzimuth < -180m)
        {
            return normalizedAzimuth + 360m;
        }

        return normalizedAzimuth;
    }

    private static string AddApiKeyToEndpointIfConfigured(string endpoint, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return endpoint;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            return endpoint;
        }

        var pathSegments = endpointUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (pathSegments.Length > 0 && string.Equals(pathSegments[0], apiKey, StringComparison.Ordinal))
        {
            return endpoint;
        }

        var estimateIndex = Array.FindIndex(pathSegments, segment =>
            string.Equals(segment, "estimate", StringComparison.OrdinalIgnoreCase));
        var escapedApiKey = Uri.EscapeDataString(apiKey);
        var nextPathSegments = estimateIndex >= 0
            ? pathSegments.Take(estimateIndex).Append(escapedApiKey).Concat(pathSegments.Skip(estimateIndex))
            : pathSegments.Prepend(escapedApiKey);
        var nextPath = "/" + string.Join("/", nextPathSegments);
        var uriBuilder = new UriBuilder(endpointUri)
        {
            Path = nextPath
        };

        return uriBuilder.Uri.ToString().TrimEnd('/');
    }

    private async Task<ForecastSolarResponse> ExecuteRequestAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ForecastSolarApiException($"Forecast.Solar hat den Request mit HTTP {(int)response.StatusCode} beantwortet.");
        }

        try
        {
            var forecastSolarResponse = JsonSerializer.Deserialize<ForecastSolarResponse>(responseBody, JsonOptions)
                ?? throw new ForecastSolarApiException("Die Forecast.Solar-Antwort ist leer.");

            if (!string.Equals(forecastSolarResponse.Message?.Type, "success", StringComparison.OrdinalIgnoreCase))
            {
                var messageText = forecastSolarResponse.Message?.Text ?? "Unbekannter Fehler";
                throw new ForecastSolarApiException($"Forecast.Solar hat Fehler zurueckgegeben: {messageText}");
            }

            return forecastSolarResponse;
        }
        catch (JsonException exception)
        {
            throw new ForecastSolarApiException("Die Forecast.Solar-Antwort konnte nicht als JSON gelesen werden.", exception);
        }
    }

    private static IReadOnlyList<PvYieldForecastSlot> MapToForecastSlots(
        ForecastSolarResponse response,
        TimeZoneInfo forecastTimeZone,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc)
    {
        var cumulativeWattHours = response.Result?.WattHours;

        if (cumulativeWattHours is null || cumulativeWattHours.Count < 2)
        {
            throw new ForecastSolarApiException("Forecast.Solar hat nicht genug PV-Ertragsdaten geliefert.");
        }

        var orderedPoints = cumulativeWattHours
            .Select(entry => new ForecastSolarEnergyPoint(ParseForecastSolarTimestamp(entry.Key, forecastTimeZone), entry.Value))
            .OrderBy(point => point.TimestampUtc)
            .ToArray();
        var intervals = CreateEnergyIntervals(orderedPoints);
        var forecastSlots = new List<PvYieldForecastSlot>();

        for (var slotStartUtc = startsAtUtc; slotStartUtc < endsAtUtc; slotStartUtc = slotStartUtc.AddMinutes(15))
        {
            var slotEndUtc = slotStartUtc.AddMinutes(15);
            var matchingInterval = intervals.SingleOrDefault(interval =>
                interval.StartsAtUtc <= slotStartUtc &&
                slotEndUtc <= interval.EndsAtUtc);

            if (matchingInterval is null)
            {
                throw new ForecastSolarApiException("Forecast.Solar hat keine PV-Ertragsdaten fuer den angeforderten Zeitraum geliefert.");
            }

            var slotShare = (decimal)(slotEndUtc - slotStartUtc).TotalHours / (decimal)(matchingInterval.EndsAtUtc - matchingInterval.StartsAtUtc).TotalHours;
            var expectedPvYieldKwh = matchingInterval.EnergyKwh * slotShare;

            forecastSlots.Add(new PvYieldForecastSlot(
                new ForecastTimeSlot(slotStartUtc, slotEndUtc),
                Math.Round(expectedPvYieldKwh, 4, MidpointRounding.AwayFromZero)));
        }

        return forecastSlots;
    }

    private static IReadOnlyList<ForecastSolarEnergyInterval> CreateEnergyIntervals(
        IReadOnlyList<ForecastSolarEnergyPoint> orderedPoints)
    {
        var intervals = new List<ForecastSolarEnergyInterval>();

        for (var index = 0; index < orderedPoints.Count - 1; index++)
        {
            var currentPoint = orderedPoints[index];
            var nextPoint = orderedPoints[index + 1];

            if (nextPoint.TimestampUtc <= currentPoint.TimestampUtc)
            {
                throw new ForecastSolarApiException("Forecast.Solar hat PV-Ertragsdaten mit ungueltiger Zeitreihenfolge geliefert.");
            }

            var energyKwh = Math.Max(0m, nextPoint.CumulativeWattHours - currentPoint.CumulativeWattHours) / 1000m;
            intervals.Add(new ForecastSolarEnergyInterval(currentPoint.TimestampUtc, nextPoint.TimestampUtc, energyKwh));
        }

        return intervals;
    }

    private static DateTimeOffset ParseForecastSolarTimestamp(string timestamp, TimeZoneInfo forecastTimeZone)
    {
        if (!DateTime.TryParseExact(
            timestamp,
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var localTimestamp))
        {
            throw new ForecastSolarApiException($"Forecast.Solar hat einen ungueltigen Zeitstempel geliefert: {timestamp}");
        }

        var unspecifiedLocalTimestamp = DateTime.SpecifyKind(localTimestamp, DateTimeKind.Unspecified);
        var utcTimestamp = TimeZoneInfo.ConvertTimeToUtc(unspecifiedLocalTimestamp, forecastTimeZone);

        return new DateTimeOffset(utcTimestamp, TimeSpan.Zero);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (string.Equals(timeZoneId, "Europe/Berlin", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new InvalidOperationException("Die PV-Forecast-Zeitzone ist ungueltig.", exception);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new InvalidOperationException("Die PV-Forecast-Zeitzone wurde auf diesem System nicht gefunden.", exception);
        }
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static void ValidateUtcRange(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Start des PV-Forecast-Zeitraums muss in UTC angegeben sein.", nameof(startsAtUtc));
        }

        if (endsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Das Ende des PV-Forecast-Zeitraums muss in UTC angegeben sein.", nameof(endsAtUtc));
        }

        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("Das Ende des PV-Forecast-Zeitraums muss nach dem Start liegen.", nameof(endsAtUtc));
        }
    }

    private sealed class ForecastSolarConfiguration
    {
        public string ApiEndpoint { get; init; } = string.Empty;

        public string? ApiKey { get; init; }

        public decimal Latitude { get; init; }

        public decimal Longitude { get; init; }

        public decimal DeclinationDegrees { get; init; }

        public decimal AzimuthDegrees { get; init; }

        public decimal PeakPowerKwp { get; init; }

        public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;
    }

    private sealed record ForecastSolarEnergyPoint(DateTimeOffset TimestampUtc, decimal CumulativeWattHours);

    private sealed record ForecastSolarEnergyInterval(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, decimal EnergyKwh);

    private sealed record ForecastSolarResponse(
        ForecastSolarResult? Result,
        ForecastSolarMessage? Message);

    private sealed record ForecastSolarResult(
        [property: JsonPropertyName("watt_hours")] IReadOnlyDictionary<string, decimal>? WattHours);

    private sealed record ForecastSolarMessage(string? Type, int Code, string? Text);
}
