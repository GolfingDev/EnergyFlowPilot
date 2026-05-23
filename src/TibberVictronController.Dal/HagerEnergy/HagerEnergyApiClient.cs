using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.HagerEnergy;

/// <summary>
/// Calls the Hager Energy API with either a configured access token or OAuth refresh-token flow.
/// </summary>
public sealed class HagerEnergyApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly DatabaseHagerEnergySettingsProvider settingsProvider;
    private readonly IControllerSettingStore controllerSettingStore;

    public HagerEnergyApiClient(
        HttpClient httpClient,
        DatabaseHagerEnergySettingsProvider settingsProvider,
        IControllerSettingStore controllerSettingStore)
    {
        this.httpClient = httpClient;
        this.settingsProvider = settingsProvider;
        this.controllerSettingStore = controllerSettingStore;
    }

    public async Task<Uri> CreateAuthorizationUriAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var endpoints = await ResolveOAuthEndpointsAsync(settings, cancellationToken);

        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            throw new InvalidOperationException("Die Hager-Energy-Client-ID ist fuer OAuth nicht konfiguriert.");
        }

        var state = CreateOAuthState();
        await controllerSettingStore.SaveSettingAsync(
            new ControllerSetting(
                ControllerSettingDefaults.HagerEnergyOAuthStateKey,
                state,
                ControllerSettingSensitivity.Sensitive,
                DateTimeOffset.UtcNow),
            cancellationToken);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = settings.ClientId,
            ["redirect_uri"] = settings.RedirectUri,
            ["scope"] = settings.Scope,
            ["state"] = state
        };
        var queryString = string.Join(
            "&",
            query
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
                .Select(entry => $"{Uri.EscapeDataString(entry.Key)}={Uri.EscapeDataString(entry.Value!)}"));
        var separator = endpoints.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";

        return new Uri($"{endpoints.AuthorizationEndpoint}{separator}{queryString}");
    }

    public async Task ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        string state,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationCode))
        {
            throw new ArgumentException("Der Hager-Energy-Authorization-Code fehlt.", nameof(authorizationCode));
        }

        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(settings.OAuthState) ||
            !string.Equals(settings.OAuthState, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Der Hager-Energy-OAuth-State ist ungueltig oder abgelaufen.");
        }

        await RequestTokenAsync(
            settings,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = authorizationCode,
                ["redirect_uri"] = settings.RedirectUri
            },
            saveReturnedTokens: true,
            cancellationToken);
    }

    public async Task<HagerEnergyCurrentValues> GetCurrentValuesAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetCurrentJsonAsync(cancellationToken);
        var settings = response.Settings;

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var gridImportWatts = HagerEnergyJsonValueReader.GetRequiredDecimal(
                document.RootElement,
                settings.GridImportJsonPath,
                HagerEnergyJsonValueReader.GridImportAliases,
                "Netzbezug");
            var pvProductionWatts = HagerEnergyJsonValueReader.GetRequiredDecimal(
                document.RootElement,
                settings.PvProductionJsonPath,
                HagerEnergyJsonValueReader.PvProductionAliases,
                "PV-Leistung");
            var batterySocPercent = HagerEnergyJsonValueReader.GetRequiredDecimal(
                document.RootElement,
                settings.BatterySocJsonPath,
                HagerEnergyJsonValueReader.BatterySocAliases,
                "Akku-SoC");

            return new HagerEnergyCurrentValues(
                gridImportWatts,
                Math.Max(0m, pvProductionWatts),
                batterySocPercent,
                response.MeasuredAtUtc);
        }
        catch (JsonException exception)
        {
            throw new HagerEnergyApiException("Die Hager-Energy-Antwort konnte nicht als JSON gelesen werden.", exception);
        }
    }

    public Task<HagerEnergyMeasuredValue> GetGridImportWattsAsync(CancellationToken cancellationToken = default)
    {
        return GetCurrentDecimalAsync(
            settings => settings.GridImportJsonPath,
            HagerEnergyJsonValueReader.GridImportAliases,
            "Netzbezug",
            value => value,
            cancellationToken);
    }

    public Task<HagerEnergyMeasuredValue> GetPvProductionWattsAsync(CancellationToken cancellationToken = default)
    {
        return GetCurrentDecimalAsync(
            settings => settings.PvProductionJsonPath,
            HagerEnergyJsonValueReader.PvProductionAliases,
            "PV-Leistung",
            value => Math.Max(0m, value),
            cancellationToken);
    }

    public Task<HagerEnergyMeasuredValue> GetBatterySocPercentAsync(CancellationToken cancellationToken = default)
    {
        return GetCurrentDecimalAsync(
            settings => settings.BatterySocJsonPath,
            HagerEnergyJsonValueReader.BatterySocAliases,
            "Akku-SoC",
            value => value,
            cancellationToken);
    }

    private async Task<HagerEnergyMeasuredValue> GetCurrentDecimalAsync(
        Func<HagerEnergySettings, string> selectConfiguredPath,
        IReadOnlySet<string> aliases,
        string displayName,
        Func<decimal, decimal> normalize,
        CancellationToken cancellationToken)
    {
        var response = await GetCurrentJsonAsync(cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var value = HagerEnergyJsonValueReader.GetRequiredDecimal(
                document.RootElement,
                selectConfiguredPath(response.Settings),
                aliases,
                displayName);

            return new HagerEnergyMeasuredValue(normalize(value), response.MeasuredAtUtc);
        }
        catch (JsonException exception)
        {
            throw new HagerEnergyApiException("Die Hager-Energy-Antwort konnte nicht als JSON gelesen werden.", exception);
        }
    }

    private async Task<HagerEnergyCurrentJsonResponse> GetCurrentJsonAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var accessToken = await GetAccessTokenAsync(settings, cancellationToken);
        var requestUri = CreateRequestUri(settings.ApiBaseUrl, $"/v1/installations/{Uri.EscapeDataString(settings.InstallationId)}/energy/current");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        AddApiKeyHeader(request, settings);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HagerEnergyApiException($"Hager Energy hat den Request mit HTTP {(int)response.StatusCode} beantwortet.");
        }

        return new HagerEnergyCurrentJsonResponse(settings, responseBody, DateTimeOffset.UtcNow);
    }

    private async Task<string> GetAccessTokenAsync(HagerEnergySettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.RefreshToken))
        {
            return await RequestTokenAsync(
                settings,
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = settings.RefreshToken
                },
                saveReturnedTokens: true,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            return settings.AccessToken;
        }

        throw new InvalidOperationException("Weder Hager-Energy-Access-Token noch Refresh-Token sind konfiguriert. Bitte Hager Energy ueber die Einstellungen verbinden.");
    }

    private async Task<string> RequestTokenAsync(
        HagerEnergySettings settings,
        Dictionary<string, string> formValues,
        bool saveReturnedTokens,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            throw new InvalidOperationException("Die Hager-Energy-Client-ID ist fuer OAuth nicht konfiguriert.");
        }

        var endpoints = await ResolveOAuthEndpointsAsync(settings, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.TokenEndpoint);

        if (string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            formValues["client_id"] = settings.ClientId;
        }

        request.Content = new FormUrlEncodedContent(formValues);

        if (!string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            var basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ClientId}:{settings.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicToken);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HagerEnergyApiException($"Hager Energy hat den Token-Request mit HTTP {(int)response.StatusCode} beantwortet.");
        }

        var tokenResponse = JsonSerializer.Deserialize<HagerEnergyTokenResponse>(responseBody, JsonOptions)
            ?? throw new HagerEnergyApiException("Die Hager-Energy-Token-Antwort ist leer.");

        if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            throw new HagerEnergyApiException("Die Hager-Energy-Token-Antwort enthaelt kein Access Token.");
        }

        if (saveReturnedTokens)
        {
            await PersistReturnedTokensAsync(tokenResponse, cancellationToken);
        }

        return tokenResponse.AccessToken;
    }

    private async Task PersistReturnedTokensAsync(HagerEnergyTokenResponse tokenResponse, CancellationToken cancellationToken)
    {
        var updatedAtUtc = DateTimeOffset.UtcNow;

        await controllerSettingStore.SaveSettingAsync(
            new ControllerSetting(
                ControllerSettingDefaults.HagerEnergyAccessTokenKey,
                tokenResponse.AccessToken,
                ControllerSettingSensitivity.Sensitive,
                updatedAtUtc),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
        {
            await controllerSettingStore.SaveSettingAsync(
                new ControllerSetting(
                    ControllerSettingDefaults.HagerEnergyRefreshTokenKey,
                    tokenResponse.RefreshToken,
                    ControllerSettingSensitivity.Sensitive,
                    updatedAtUtc),
                cancellationToken);
        }

        await controllerSettingStore.SaveSettingAsync(
            new ControllerSetting(
                ControllerSettingDefaults.HagerEnergyOAuthStateKey,
                null,
                ControllerSettingSensitivity.Sensitive,
                updatedAtUtc),
            cancellationToken);
    }

    private static string CreateOAuthState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);

        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);
    }

    private static Uri CreateRequestUri(string baseUrl, string relativePath)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Die Hager-Energy-API-Basis-URL ist keine gueltige absolute URL.");
        }

        return new Uri(baseUri, relativePath);
    }

    private async Task<HagerEnergyOAuthEndpoints> ResolveOAuthEndpointsAsync(
        HagerEnergySettings settings,
        CancellationToken cancellationToken)
    {
        if (!IsDiscoveryEndpoint(settings.AuthorizationEndpoint))
        {
            return new HagerEnergyOAuthEndpoints(settings.AuthorizationEndpoint, settings.TokenEndpoint);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, settings.AuthorizationEndpoint);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HagerEnergyApiException($"Hager Energy hat die OAuth-Discovery mit HTTP {(int)response.StatusCode} beantwortet.");
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var authorizationEndpoint = ReadRequiredString(document.RootElement, "authorization_endpoint", "Authorization-Endpunkt");
            var tokenEndpoint = ReadRequiredString(document.RootElement, "token_endpoint", "Token-Endpunkt");

            return new HagerEnergyOAuthEndpoints(authorizationEndpoint, tokenEndpoint);
        }
        catch (JsonException exception)
        {
            throw new HagerEnergyApiException("Die Hager-Energy-OAuth-Discovery konnte nicht als JSON gelesen werden.", exception);
        }
    }

    private static bool IsDiscoveryEndpoint(string endpoint)
    {
        return endpoint.Contains("/.well-known/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRequiredString(JsonElement root, string propertyName, string displayName)
    {
        if (root.TryGetProperty(propertyName, out var valueElement) &&
            valueElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(valueElement.GetString()))
        {
            return valueElement.GetString()!;
        }

        throw new HagerEnergyApiException($"Die Hager-Energy-OAuth-Discovery enthaelt keinen {displayName}.");
    }

    private static void AddApiKeyHeader(HttpRequestMessage request, HagerEnergySettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Add("api_key", settings.ApiKey);
        }
    }

    private sealed record HagerEnergyTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken);

    private sealed record HagerEnergyOAuthEndpoints(
        string AuthorizationEndpoint,
        string TokenEndpoint);

    private sealed record HagerEnergyCurrentJsonResponse(
        HagerEnergySettings Settings,
        string Body,
        DateTimeOffset MeasuredAtUtc);
}
