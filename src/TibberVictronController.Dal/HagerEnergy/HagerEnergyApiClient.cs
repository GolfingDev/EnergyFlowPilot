using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TibberVictronController.Dal.HagerEnergy;

/// <summary>
/// Calls the Hager Energy (E3/DC) Cloud API using the OAuth2 Client Credentials flow.
/// Tokens expire after 5 minutes and are cached in-memory to avoid redundant requests.
/// </summary>
public sealed class HagerEnergyApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly DatabaseHagerEnergySettingsProvider settingsProvider;
    private readonly HagerEnergyTokenCache tokenCache;

    public HagerEnergyApiClient(
        HttpClient httpClient,
        DatabaseHagerEnergySettingsProvider settingsProvider,
        HagerEnergyTokenCache tokenCache)
    {
        this.httpClient = httpClient;
        this.settingsProvider = settingsProvider;
        this.tokenCache = tokenCache;
    }

    public async Task<HagerEnergyCurrentValues> GetCurrentValuesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);

        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("Die E3/DC-Zugangsdaten (Client-ID, Client-Secret, Installation-ID) sind nicht vollstaendig konfiguriert.");
        }

        var accessToken = await GetAccessTokenAsync(settings, cancellationToken);
        var requestUri = CreateRequestUri(settings.ApiBaseUrl, $"/v1/installations/{Uri.EscapeDataString(settings.InstallationId!)}/energy/current");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HagerEnergyApiException($"Die E3/DC-API hat den Telemetrie-Request mit HTTP {(int)response.StatusCode} beantwortet.");
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
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
            var batteryPowerWatts = HagerEnergyJsonValueReader.TryGetDecimal(
                document.RootElement,
                HagerEnergyJsonValueReader.BatteryPowerAliases);

            return new HagerEnergyCurrentValues(
                gridImportWatts,
                Math.Max(0m, pvProductionWatts),
                batterySocPercent,
                batteryPowerWatts,
                DateTimeOffset.UtcNow);
        }
        catch (JsonException exception)
        {
            throw new HagerEnergyApiException("Die E3/DC-Antwort konnte nicht als JSON verarbeitet werden.", exception);
        }
    }

    public async Task<string[]> GetInstallationIdsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);

        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("Die E3/DC-Zugangsdaten sind nicht vollstaendig konfiguriert.");
        }

        var accessToken = await GetAccessTokenAsync(settings, cancellationToken);
        var requestUri = CreateRequestUri(settings.ApiBaseUrl, "/v1/installations");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HagerEnergyApiException($"Die E3/DC-API hat den Installationen-Request mit HTTP {(int)response.StatusCode} beantwortet.");
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            // Try common response shapes: array at root, or array under "data" or "installations"
            var array = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array ? dataEl
                : root.TryGetProperty("installations", out var instEl) && instEl.ValueKind == JsonValueKind.Array ? instEl
                : default;

            if (array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return array.EnumerateArray()
                .Select(item =>
                    item.TryGetProperty("id", out var id) ? id.GetString()
                    : item.TryGetProperty("installationId", out var instId) ? instId.GetString()
                    : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new HagerEnergyApiException("Die E3/DC-Installationen-Antwort konnte nicht als JSON verarbeitet werden.", exception);
        }
    }

    private async Task<string> GetAccessTokenAsync(HagerEnergySettings settings, CancellationToken cancellationToken)
    {
        var cached = tokenCache.GetCachedToken();
        if (cached is not null)
        {
            return cached;
        }

        await tokenCache.Semaphore.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring the lock — another caller may have refreshed already
            cached = tokenCache.GetCachedToken();
            if (cached is not null)
            {
                return cached;
            }

            return await RequestNewTokenAsync(settings, cancellationToken);
        }
        finally
        {
            tokenCache.Semaphore.Release();
        }
    }

    private async Task<string> RequestNewTokenAsync(HagerEnergySettings settings, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenEndpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = settings.ClientId!,
            ["client_secret"] = settings.ClientSecret!
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HagerEnergyApiException($"Die E3/DC-Authentifizierung hat mit HTTP {(int)response.StatusCode} geantwortet.");
        }

        var tokenResponse = JsonSerializer.Deserialize<HagerEnergyTokenResponse>(responseBody, JsonOptions)
            ?? throw new HagerEnergyApiException("Die E3/DC-Token-Antwort ist leer.");

        if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            throw new HagerEnergyApiException("Die E3/DC-Token-Antwort enthaelt kein Access Token.");
        }

        var expiresIn = tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : 300;
        tokenCache.StoreToken(tokenResponse.AccessToken, expiresIn);

        return tokenResponse.AccessToken;
    }

    private static Uri CreateRequestUri(string baseUrl, string relativePath)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Die E3/DC-API-Basis-URL ist keine gueltige absolute URL.");
        }

        return new Uri(baseUri, relativePath);
    }

    private sealed record HagerEnergyTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
