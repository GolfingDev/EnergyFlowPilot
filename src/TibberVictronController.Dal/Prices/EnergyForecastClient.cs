using System.Globalization;
using System.Net;
using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Prices;

/// <summary>
/// energyforecast.de API v2: quarter-hourly exchange prices for whole local days, published auction results where
/// available, otherwise a forecast (Pro plan: today plus six days). Read without tariff (fixed cost and VAT 0), so the
/// values compare with any supplier; the token travels as query parameter, as the API demands, and never appears in
/// messages.
/// </summary>
public sealed class EnergyForecastClient(HttpClient http, IControllerSettingStore settings) : IPriceForecastSource
{
    public const string BaseUrl = "https://www.energyforecast.de/api/v2/forecast";

    public async Task<IReadOnlyList<PriceForecastPoint>?> GetAsync(CancellationToken cancellationToken = default)
    {
        var token = (await settings.GetSettingAsync(ControllerSettingDefaults.PriceForecastTokenKey, cancellationToken))?.Value;
        if (string.IsNullOrWhiteSpace(token)) return null;
        var zone = (await settings.GetSettingAsync(ControllerSettingDefaults.EnergyChartsZoneKey, cancellationToken))?.Value;
        var url = $"{BaseUrl}?market_zone={Uri.EscapeDataString(string.IsNullOrWhiteSpace(zone) ? "DE-LU" : zone)}&fixed_cost_cent=0&vat=0" +
            $"&token={Uri.EscapeDataString(token.Trim())}";
        using var response = await http.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("energyforecast.de lehnt den Token ab (HTTP 401). Token in den Einstellungen prüfen.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("energyforecast.de meldet: Abfragelimit für heute erreicht (HTTP 429).");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"energyforecast.de-Abfrage fehlgeschlagen (HTTP {(int)response.StatusCode}).");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("energyforecast.de liefert keine Preise (Feld 'data' fehlt).");
        var points = new List<PriceForecastPoint>();
        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(start.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var startsAt) ||
                !entry.TryGetProperty("price_ct_kwh", out var price) || !price.TryGetDecimal(out var cents))
                continue;
            var origin = entry.TryGetProperty("price_origin", out var originElement) ? originElement.GetString() : null;
            points.Add(new PriceForecastPoint(startsAt.ToUniversalTime(), cents / 100m, origin != "market"));
        }

        return points;
    }
}
