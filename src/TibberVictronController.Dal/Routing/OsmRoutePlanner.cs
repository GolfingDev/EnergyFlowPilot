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
    public const string UserAgent = "EnergyFlowPilot/1.0 (private home energy controller)";

    public async Task<RouteEstimate> EstimateAsync(GeoPoint origin, string destination, bool placesOnly = false, CancellationToken cancellationToken = default)
    {
        var (target, address) = await GeocodeAsync(destination, placesOnly, cancellationToken);
        return await RouteToAsync(origin, target, address, cancellationToken);
    }

    public async Task<RouteEstimate> RouteToAsync(GeoPoint origin, GeoPoint target, string address, CancellationToken cancellationToken = default)
    {
        var destination = address;
        var url = OsrmUrl + string.Create(CultureInfo.InvariantCulture,
            $"{origin.Longitude},{origin.Latitude};{target.Longitude},{target.Latitude}?overview=false");
        using var document = await GetJsonAsync(url, "Routenberechnung", cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var code) && code.GetString() != "Ok" ||
            !root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() == 0)
            throw new InvalidOperationException($"Fuer '{destination}' wurde keine Autoroute gefunden.");
        var route = routes[0];
        return new RouteEstimate(address, target,
            Math.Round(route.GetProperty("distance").GetDouble() / 1000d, 1),
            Math.Round(route.GetProperty("duration").GetDouble() / 60d));
    }

    private async Task<(GeoPoint Point, string Address)> GeocodeAsync(string destination, bool placesOnly, CancellationToken cancellationToken)
    {
        // featureType=settlement restricts hits to cities, towns and villages, so "in Ruhe" does not become a street.
        var url = $"{NominatimUrl}?format=jsonv2&limit=1&accept-language=de&q={Uri.EscapeDataString(destination)}" +
                  (placesOnly ? "&featureType=settlement" : string.Empty);
        using var document = await GetJsonAsync(url, "Adresssuche", cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidOperationException($"Das Ziel '{destination}' wurde nicht gefunden. Bitte genauer angeben (Ort, Strasse).");
        var hit = root[0];
        return (new GeoPoint(Coordinate(hit, "lat"), Coordinate(hit, "lon")),
            hit.TryGetProperty("display_name", out var name) ? name.GetString() ?? destination : destination);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string purpose, CancellationToken cancellationToken)
    {
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
