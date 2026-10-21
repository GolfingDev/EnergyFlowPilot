using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Octopus;

/// <summary>German dynamicOctopus only. Never falls back to UK rates, spot prices or another supplier.</summary>
public sealed class OctopusPriceForecastProvider(
    HttpClient httpClient, IControllerSettingStore settings, OctopusPriceCache cache, IUtcClock clock)
    : IElectricityPriceForecastProvider
{
    public const string Endpoint = "https://api.oeg-kraken.energy/v1/graphql/";
    public string Source => "octopusGermany";
    private const string TokenQuery = """
        mutation Token($input: ObtainJSONWebTokenInput!) {
          obtainKrakenToken(input: $input) { token }
        }
        """;
    // Keep the documented DE path. Do not concatenate customer input into GraphQL source.
    private const string PriceQuery = """
        query Prices($accountNumber: String!) {
          account(accountNumber: $accountNumber) {
            properties { electricityMalos { agreements { unitRateForecast {
              validFrom validTo unitRateInformation {
                ... on TimeOfUseProductUnitRateInformation {
                  rates { latestGrossUnitRateCentsPerKwh }
                }
              }
            } } } }
          }
        }
        """;

    public async Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(
        DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero || endsAtUtc.Offset != TimeSpan.Zero || startsAtUtc >= endsAtUtc)
            throw new ArgumentException("Der Strompreiszeitraum muss in UTC vorwaerts verlaufen.");
        var key = (await settings.GetSettingAsync(ControllerSettingDefaults.OctopusApiKeyKey, cancellationToken))?.Value;
        var account = (await settings.GetSettingAsync(ControllerSettingDefaults.OctopusAccountNumberKey, cancellationToken))?.Value;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(account) ||
            account == ControllerSettingDefaults.OctopusAccountNumberNotConfigured)
            throw new InvalidOperationException("Octopus Deutschland benoetigt API-Key und Kundennummer.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { key, account }))));
        await cache.Gate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.UtcNow;
            if (cache.Key != fingerprint)
            {
                cache.Key = fingerprint;
                cache.Token = null;
                cache.Prices = [];
                cache.PricesExpireAtUtc = default;
                cache.RetryAfterUtc = default;
            }
            if (now < cache.RetryAfterUtc)
                throw new InvalidOperationException("Octopus-Preisabruf voruebergehend gesperrt; erneuter Versuch nach kurzer Wartezeit.");
            if (now >= cache.PricesExpireAtUtc)
            {
                try
                {
                    if (cache.Token is null || now >= cache.TokenExpiresAtUtc)
                        await AuthenticateAsync(key, cancellationToken);
                    using var response = await GetPricesAsync(account, cancellationToken);
                    cache.Prices = ParsePrices(response.RootElement);
                    cache.PricesExpireAtUtc = clock.UtcNow.AddMinutes(10);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Bound retries across battery, dashboard and warm-water consumers, without stale supplier fallback.
                    cache.Token = null;
                    cache.Prices = [];
                    cache.PricesExpireAtUtc = default;
                    cache.RetryAfterUtc = clock.UtcNow.AddMinutes(1);
                    throw;
                }
            }
            return cache.Prices.Where(p => p.TimeSlot.StartsAtUtc < endsAtUtc && p.TimeSlot.EndsAtUtc > startsAtUtc).ToArray();
        }
        finally { cache.Gate.Release(); }
    }

    private async Task AuthenticateAsync(string key, CancellationToken ct)
    {
        using var response = await SendAsync(TokenQuery, new { input = new { APIKey = key } }, null, ct);
        if (!response.RootElement.GetProperty("data").TryGetProperty("obtainKrakenToken", out var authentication) ||
            authentication.ValueKind != JsonValueKind.Object ||
            !authentication.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(token.GetString()))
            throw new InvalidOperationException("Octopus hat kein gueltiges Zugriffstoken geliefert.");
        cache.Token = token.GetString();
        cache.TokenExpiresAtUtc = clock.UtcNow.AddMinutes(45);
    }

    private Task<JsonDocument> GetPricesAsync(string account, CancellationToken ct) =>
        SendAsync(PriceQuery, new { accountNumber = account }, cache.Token, ct);

    private async Task<JsonDocument> SendAsync(string query, object variables, string? token, CancellationToken ct)
    {
        // GraphQL input names are case-sensitive: web defaults would turn APIKey into apiKey.
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        { Content = JsonContent.Create(new { query, variables }, options: JsonSerializerOptions.Default) };
        if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", token);
        using var response = await httpClient.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Octopus-Zugriff verweigert; API-Key oder Berechtigungen pruefen.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Octopus hat den Abruf mit HTTP {(int)response.StatusCode} beantwortet.");
        JsonDocument document;
        try { document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); }
        catch (JsonException)
        { throw new InvalidOperationException("Die Octopus-Antwort ist kein gueltiges JSON."); }
        var root = document.RootElement;
        // GraphQL errors may echo input. Never put the response body or remote error messages into logs.
        if (root.ValueKind != JsonValueKind.Object ||
            (root.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null &&
             (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() > 0)) ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidOperationException("Octopus hat einen GraphQL-Fehler oder keine verwendbaren Daten geliefert.");
        }
        return document;
    }

    internal static IReadOnlyList<TibberPriceForecastSlot> ParsePrices(JsonElement root)
    {
        try
        {
            var candidates = new List<List<TibberPriceForecastSlot>>();
            foreach (var property in root.GetProperty("data").GetProperty("account").GetProperty("properties").EnumerateArray())
            foreach (var malo in property.GetProperty("electricityMalos").EnumerateArray())
            {
                var slots = new List<TibberPriceForecastSlot>();
                foreach (var agreement in malo.GetProperty("agreements").EnumerateArray())
                {
                    if (!agreement.TryGetProperty("unitRateForecast", out var forecast) || forecast.ValueKind == JsonValueKind.Null) continue;
                    foreach (var entry in forecast.EnumerateArray())
                    {
                        var start = entry.GetProperty("validFrom").GetDateTimeOffset().ToUniversalTime();
                        var end = entry.GetProperty("validTo").GetDateTimeOffset().ToUniversalTime();
                        if (end - start != TimeSpan.FromMinutes(15) || start.Ticks % TimeSpan.FromMinutes(15).Ticks != 0)
                            throw new InvalidOperationException("Octopus-Preise muessen eindeutige 15-Minuten-Zeitraeume enthalten.");
                        var rates = entry.GetProperty("unitRateInformation").GetProperty("rates");
                        if (rates.GetArrayLength() != 1)
                            throw new InvalidOperationException("Octopus liefert mehrere oder keine Preiszonen; automatische Auswahl ist nicht sicher.");
                        var raw = rates[0].GetProperty("latestGrossUnitRateCentsPerKwh");
                        var price = raw.ValueKind == JsonValueKind.String
                            ? decimal.Parse(raw.GetString()!, CultureInfo.InvariantCulture) : raw.GetDecimal();
                        slots.Add(new TibberPriceForecastSlot(new ForecastTimeSlot(start, end), price / 100m, "EUR"));
                    }
                }
                if (slots.Count > 0) candidates.Add(slots);
            }
            if (candidates.Count != 1)
                throw new InvalidOperationException("Octopus liefert keine oder mehrere Strom-Marktlokationen mit Preisen. dynamicOctopus-Vertrag und Zuordnung pruefen.");
            var ordered = candidates[0].OrderBy(p => p.TimeSlot.StartsAtUtc).ToArray();
            if (ordered.Zip(ordered.Skip(1), (a, b) => a.TimeSlot.EndsAtUtc > b.TimeSlot.StartsAtUtc).Any(overlap => overlap))
                throw new InvalidOperationException("Octopus liefert ueberlappende Preise oder Vertraege; keine automatische Auswahl.");
            // Missing future intervals remain missing; consumers already distinguish forecasts from published prices.
            return ordered;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            if (ex is InvalidOperationException && ex.Message.StartsWith("Octopus", StringComparison.Ordinal)) throw;
            throw new InvalidOperationException("Die Octopus-Preisantwort entspricht nicht dem erwarteten deutschen Tarifformat.");
        }
    }
}
