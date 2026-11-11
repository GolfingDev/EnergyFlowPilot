using System.Globalization;
using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Routing;

/// <summary>
/// Free-text destination to driving distance via OpenStreetMap services: Nominatim for geocoding, the public OSRM
/// server for the route. Both are free community services with usage policies (identifying User-Agent, at most one
/// request per second, no bulk use); one trip entry makes exactly two requests, so the controller stays well inside.
/// </summary>
public sealed class OsmRoutePlanner(HttpClient httpClient) : IRoutePlanner
{
    public const string NominatimUrl = "https://nominatim.openstreetmap.org/search";
    public const string OsrmUrl = "https://router.project-osrm.org/route/v1/driving/";
    private const double MaximumSlowerFactor = 1.2;
    public const string UserAgent = "EnergyFlowPilot/1.0 (private home energy controller)";
    // About 50 km around home at central European latitudes; the nearest of up to 20 hits inside wins, because
    // Nominatim ranks by importance, not distance (a Frankfurt store would otherwise beat the one in town).
    private const double LocalBoxLatitude = 0.5;
    private const double LocalBoxLongitude = 0.75;
    private const int LocalCandidates = 20;
    // Each variant costs up to two searches at one per second; three cover "venue, street, town".
    private const int MaximumQueryVariants = 3;
    private static readonly HashSet<string> CountryNames = new(StringComparer.OrdinalIgnoreCase)
        { "Deutschland", "Germany", "DE", "Österreich", "Austria", "Schweiz", "Switzerland" };

    public async Task<RouteEstimate> EstimateAsync(GeoPoint origin, string destination, bool placesOnly = false, CancellationToken cancellationToken = default)
    {
        var (target, address) = await GeocodeAsync(destination, placesOnly, origin, cancellationToken);
        return await RouteToAsync(origin, target, address, cancellationToken);
    }

    public async Task<RouteEstimate> RouteToAsync(GeoPoint origin, GeoPoint target, string address, CancellationToken cancellationToken = default)
    {
        var destination = address;
        var url = OsrmUrl + string.Create(CultureInfo.InvariantCulture,
            $"{origin.Longitude},{origin.Latitude};{target.Longitude},{target.Latitude}?overview=false&alternatives=3");
        using var document = await GetJsonAsync(url, "Routenberechnung", cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var code) && code.GetString() != "Ok" ||
            !root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() == 0)
            throw new InvalidOperationException($"Fuer '{destination}' wurde keine Autoroute gefunden.");
        // OSRM returns the fastest route first. A motorway detour that saves seconds is not what people drive and costs
        // energy (operator 2026-11-01: 12 km via A66 instead of 5.7 km via K72, 18 s faster). Of the routes at most
        // 20 % slower than the fastest, the shortest is taken.
        var candidates = routes.EnumerateArray()
            .Select(route => (Distance: route.GetProperty("distance").GetDouble(), Duration: route.GetProperty("duration").GetDouble()))
            .ToArray();
        var fastest = candidates.Min(route => route.Duration);
        var chosen = candidates.Where(route => route.Duration <= fastest * MaximumSlowerFactor).MinBy(route => route.Distance);
        return new RouteEstimate(address, target, Math.Round(chosen.Distance / 1000d, 1), Math.Round(chosen.Duration / 60d));
    }

    /// <summary>
    /// Searches around home first (about 50 km): a calendar location like "Lidl" or "Sporthalle" means the nearest
    /// one, not the first match in the country. Only if nothing is found there the whole map is searched.
    /// </summary>
    private async Task<(GeoPoint Point, string Address)> GeocodeAsync(string destination, bool placesOnly, GeoPoint near, CancellationToken cancellationToken)
    {
        var viewbox = string.Create(CultureInfo.InvariantCulture,
            $"&limit={LocalCandidates}&viewbox={near.Longitude - LocalBoxLongitude},{near.Latitude + LocalBoxLatitude},{near.Longitude + LocalBoxLongitude},{near.Latitude - LocalBoxLatitude}&bounded=1");
        foreach (var query in QueryVariants(destination))
        {
            // featureType=settlement restricts hits to cities, towns and villages, so "in Ruhe" does not become a street.
            var baseUrl = $"{NominatimUrl}?format=jsonv2&accept-language=de&q={Uri.EscapeDataString(query)}" +
                          (placesOnly ? "&featureType=settlement" : string.Empty);
            var hit = await SearchAsync(baseUrl + viewbox, near, cancellationToken) ?? await SearchAsync(baseUrl + "&limit=1", null, cancellationToken);
            if (hit is { } found) return (found.Point, found.Address ?? destination);
        }

        throw new InvalidOperationException($"Das Ziel '{destination}' wurde nicht gefunden. Bitte genauer angeben (Ort, Strasse).");
    }

    /// <summary>
    /// The location as written, then without its leading parts: calendar locations often start with the name of the
    /// venue ("Alte Schule Fulda, Jesuitenplatz 2, 36037 Fulda"), and Nominatim finds nothing for the whole text
    /// (operator 2026-11-11), while the address alone is found. A country alone is never searched.
    /// </summary>
    public static IReadOnlyList<string> QueryVariants(string destination)
    {
        var parts = destination.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var variants = new List<string> { destination.Trim() };
        for (var skip = 1; skip < parts.Length && variants.Count < MaximumQueryVariants; skip++)
        {
            var rest = parts[skip..];
            if (rest.All(CountryNames.Contains)) break;
            variants.Add(string.Join(", ", rest));
        }

        return variants;
    }

    private async Task<(GeoPoint Point, string? Address)?> SearchAsync(string url, GeoPoint? nearest, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(url, "Adresssuche", cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
        var hits = root.EnumerateArray().Select(hit => (Point: new GeoPoint(Coordinate(hit, "lat"), Coordinate(hit, "lon")),
            Address: hit.TryGetProperty("display_name", out var name) ? name.GetString() : null)).ToArray();
        return nearest is null ? hits[0] : hits.MinBy(hit => DistanceKm(nearest, hit.Point));
    }

    // Great-circle distance; enough to pick the nearest candidate.
    private static double DistanceKm(GeoPoint a, GeoPoint b)
    {
        const double EarthRadiusKm = 6371;
        double Radians(double degrees) => degrees * Math.PI / 180;
        var dLat = Radians(b.Latitude - a.Latitude);
        var dLon = Radians(b.Longitude - a.Longitude);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Radians(a.Latitude)) * Math.Cos(Radians(b.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Sqrt(h));
    }

    public async Task<GeoPoint?> FindSettlementNearAsync(string name, GeoPoint near, double maximumKm, CancellationToken cancellationToken = default)
    {
        var url = $"{NominatimUrl}?format=jsonv2&accept-language=de&featureType=settlement&q={Uri.EscapeDataString(name)}" +
                  string.Create(CultureInfo.InvariantCulture,
                      $"&limit={LocalCandidates}&viewbox={near.Longitude - LocalBoxLongitude},{near.Latitude + LocalBoxLatitude},{near.Longitude + LocalBoxLongitude},{near.Latitude - LocalBoxLatitude}&bounded=1");
        var hit = await SearchAsync(url, near, cancellationToken);
        return hit is { } found && DistanceKm(near, found.Point) <= maximumKm ? found.Point : null;
    }

    // Nominatim's usage policy: at most one request per second from this application, whoever asks.
    private static readonly SemaphoreSlim NominatimGate = new(1, 1);
    private static DateTimeOffset nominatimLastRequestUtc = DateTimeOffset.MinValue;

    private async Task<JsonDocument> GetJsonAsync(string url, string purpose, CancellationToken cancellationToken)
    {
        var nominatim = url.StartsWith(NominatimUrl, StringComparison.Ordinal);
        if (nominatim)
        {
            await NominatimGate.WaitAsync(cancellationToken);
            try
            {
                var wait = nominatimLastRequestUtc + TimeSpan.FromSeconds(1) - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
                nominatimLastRequestUtc = DateTimeOffset.UtcNow;
            }
            finally
            {
                NominatimGate.Release();
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{purpose} bei OpenStreetMap fehlgeschlagen (HTTP {(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    // Nominatim returns coordinates as strings.
    private static double Coordinate(JsonElement hit, string name) =>
        hit.TryGetProperty(name, out var value) &&
        double.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new InvalidOperationException("OpenStreetMap liefert keine Koordinaten fuer das Ziel.");
}
