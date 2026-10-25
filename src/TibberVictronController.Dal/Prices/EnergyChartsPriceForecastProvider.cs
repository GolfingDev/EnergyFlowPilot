using System.Globalization;
using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Dal.Prices;

/// <summary>Fraunhofer ISE spot prices converted to the configured gross retail tariff.</summary>
public sealed class EnergyChartsPriceForecastProvider(HttpClient http, IControllerSettingStore settings,
    IUtcClock clock, EnergyChartsPriceCache cache) : IElectricityPriceForecastProvider, IEnergyMarketPriceProvider
{
    public string Source => "energyCharts";

    public async Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(DateTimeOffset start,
        DateTimeOffset end, CancellationToken cancellationToken = default)
    {
        if (start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero || end <= start)
            throw new ArgumentException("Der Strompreiszeitraum muss in UTC vorwaerts verlaufen.");
        async Task<string?> Setting(string key) => (await settings.GetSettingAsync(key, cancellationToken))?.Value;
        if (!decimal.TryParse(await Setting(ControllerSettingDefaults.EnergyChartsSurchargeKey), NumberStyles.Number, CultureInfo.InvariantCulture, out var surcharge) || surcharge is < 0 or > 5)
            throw new InvalidOperationException("Fuer Energy-Charts muss der tarifliche Bruttoaufschlag in EUR/kWh eingerichtet sein; 0 ist eine ausdrueckliche Angabe.");
        if (!decimal.TryParse(await Setting(ControllerSettingDefaults.EnergyChartsVatKey), NumberStyles.Number, CultureInfo.InvariantCulture, out var vat) || vat is < 0 or > 100)
            throw new InvalidOperationException("Der Mehrwertsteuersatz fuer Energy-Charts ist ungueltig.");
        var slotTicks = TimeSpan.FromMinutes(15).Ticks;
        var first = new DateTimeOffset(start.UtcTicks - start.UtcTicks % slotTicks, TimeSpan.Zero);
        var last = new DateTimeOffset(end.UtcTicks + (slotTicks - end.UtcTicks % slotTicks) % slotTicks, TimeSpan.Zero);
        var market = await GetMarketPricesAsync(new(first, last, await Setting(ControllerSettingDefaults.EnergyChartsZoneKey) ?? "DE-LU"), cancellationToken);
        return market.Where(p => p.TimeSlot.StartsAtUtc < end && p.TimeSlot.EndsAtUtc > start).Select(p => new TibberPriceForecastSlot(p.TimeSlot,
            DynamicTariffPrice.GrossWorkPrice(p.NetPricePerKwh, vat, surcharge), "EUR")).ToArray();
    }

    public async Task<IReadOnlyList<EnergyMarketPrice>> GetMarketPricesAsync(EnergyMarketPriceRequest request, CancellationToken token = default)
    {
        if (request.FromUtc.Offset != TimeSpan.Zero || request.ToUtc.Offset != TimeSpan.Zero || request.ToUtc <= request.FromUtc ||
            request.ToUtc - request.FromUtc > TimeSpan.FromDays(32))
            throw new ArgumentException("Der Boersenpreiszeitraum muss in UTC liegen und darf hoechstens 32 Tage umfassen.");
        var zone = request.BiddingZone;
        if (string.IsNullOrWhiteSpace(zone) || zone.Length > 16 || zone.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c) && c != '-'))
            throw new ArgumentException("Ungueltige Energy-Charts-Gebotszone.");
        // Canonical UTC days share cached requests across moving forecasts. Filter back to exact caller boundaries below.
        var from = request.FromUtc.UtcDateTime.Date;
        var to = request.ToUtc.UtcDateTime.Date.AddDays(1);
        var lastRequestedDay = clock.UtcNow.UtcDateTime.Date.AddDays(1);
        if (to > lastRequestedDay) to = lastRequestedDay;
        if (from > to) return [];
        var key = $"{zone}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";
        await cache.Gate.WaitAsync(token);
        try
        {
            if (!cache.Entries.TryGetValue(key, out var entry) || entry.Expires <= clock.UtcNow)
            {
                if (cache.RetryAfter.TryGetValue(key, out var retry) && clock.UtcNow < retry)
                    throw new InvalidOperationException("Energy-Charts ist voruebergehend nicht verfuegbar; erneuter Abruf nach kurzer Wartezeit.");
                try
                {
                    using var response = await http.GetAsync($"https://api.energy-charts.info/price?bzn={Uri.EscapeDataString(zone)}&start={from:yyyy-MM-dd}&end={to:yyyy-MM-dd}", token);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException($"Energy-Charts konnte nicht geladen werden (HTTP {(int)response.StatusCode}).");
                    using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
                    entry = (clock.UtcNow.AddMinutes(30), Parse(json.RootElement));
                    // Bound the singleton cache even when settings/periods are changed repeatedly.
                    if (cache.Entries.Count >= 32) cache.Entries.Clear();
                    if (cache.RetryAfter.Count >= 32) cache.RetryAfter.Clear();
                    cache.Entries[key] = entry;
                    cache.RetryAfter.Remove(key);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    if (cache.RetryAfter.Count >= 32) cache.RetryAfter.Clear();
                    cache.RetryAfter[key] = clock.UtcNow.AddMinutes(1);
                    throw new InvalidOperationException("Zeitueberschreitung beim Energy-Charts-Preisabruf; erneuter Versuch nach kurzer Wartezeit.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or ArgumentException or FormatException)
                {
                    if (cache.RetryAfter.Count >= 32) cache.RetryAfter.Clear();
                    cache.RetryAfter[key] = clock.UtcNow.AddMinutes(1);
                    throw new InvalidOperationException("Keine gueltigen Energy-Charts-Preise verfuegbar. " + (error is HttpRequestException ? "Netzwerkfehler beim Preisabruf." : error.Message), error);
                }
            }
            return entry.Prices.Where(p => p.TimeSlot.StartsAtUtc >= request.FromUtc && p.TimeSlot.EndsAtUtc <= request.ToUtc).ToArray();
        }
        finally { cache.Gate.Release(); }
    }

    private static IReadOnlyList<EnergyMarketPrice> Parse(JsonElement root)
    {
        if (!root.TryGetProperty("unit", out var unit) || unit.GetString()?.Replace(" ", "") != "EUR/MWh" ||
            !root.TryGetProperty("unix_seconds", out var times) || times.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("price", out var values) || values.ValueKind != JsonValueKind.Array ||
            times.GetArrayLength() < 2 || times.GetArrayLength() != values.GetArrayLength())
            throw new InvalidOperationException("Energy-Charts liefert keine passende EUR/MWh-Preisreihe.");
        var timestamps = times.EnumerateArray().Select(t => DateTimeOffset.FromUnixTimeSeconds(t.GetInt64())).ToArray();
        var result = new List<EnergyMarketPrice>();
        for (var i = 0; i < timestamps.Length; i++)
        {
            var interval = i + 1 < timestamps.Length ? timestamps[i + 1] - timestamps[i] : timestamps[i] - timestamps[i - 1];
            if (interval.TotalMinutes is not (15 or 30 or 60) || timestamps[i].UtcTicks % TimeSpan.FromMinutes(15).Ticks != 0)
                throw new InvalidOperationException("Energy-Charts enthaelt doppelte, ungeordnete oder lueckenhafte Zeitpunkte.");
            if (values[i].ValueKind == JsonValueKind.Null) continue; // Unpublished prices must remain missing.
            if (!values[i].TryGetDecimal(out var price)) throw new InvalidOperationException("Energy-Charts enthaelt einen ungueltigen Preis.");
            for (var at = timestamps[i]; at < timestamps[i].Add(interval); at = at.AddMinutes(15))
                result.Add(new(new(at, at.AddMinutes(15)), price / 1000m));
        }
        return result;
    }
}
