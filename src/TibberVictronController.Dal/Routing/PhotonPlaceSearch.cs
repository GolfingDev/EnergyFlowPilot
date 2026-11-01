using System.Globalization;
using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Routing;

/// <summary>
/// Search-as-you-type over OpenStreetMap data with Photon (komoot). Nominatim forbids autocomplete, Photon is built
/// for it; the public instance asks for fair use, so the browser waits for a typing pause and the server caches.
/// Results near home come first.
/// </summary>
public sealed class PhotonPlaceSearch(HttpClient httpClient) : IPlaceSearch
{
    public const string PhotonUrl = "https://photon.komoot.io/api/";
    private const int Limit = 6;

    public async Task<IReadOnlyList<PlaceSuggestion>> SuggestAsync(string query, GeoPoint? near, CancellationToken cancellationToken = default)
    {
        var url = $"{PhotonUrl}?q={Uri.EscapeDataString(query)}&limit={Limit}&lang=de" +
                  (near is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"&lat={near.Latitude}&lon={near.Longitude}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(OsmRoutePlanner.UserAgent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Adressvorschlaege nicht verfuegbar (HTTP {(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array) return [];
        return features.EnumerateArray().Select(Parse).OfType<PlaceSuggestion>().DistinctBy(place => place.Label).ToArray();
    }

    private static PlaceSuggestion? Parse(JsonElement feature)
    {
        if (!feature.TryGetProperty("geometry", out var geometry) || !geometry.TryGetProperty("coordinates", out var coordinates) ||
            coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() < 2 ||
            !feature.TryGetProperty("properties", out var properties)) return null;
        string? Text(string name) => properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        // "Name, Strasse Nr, PLZ Ort" without repeating parts (a town's name is also its city).
        var street = Text("street") is { } road ? (Text("housenumber") is { } number ? $"{road} {number}" : road) : null;
        var town = string.Join(" ", new[] { Text("postcode"), Text("city") ?? Text("district") ?? Text("county") }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var parts = new[] { Text("name"), street, town, Text("country") == "Deutschland" ? null : Text("country") }
            .Where(part => !string.IsNullOrWhiteSpace(part)).Distinct().ToArray();
        if (parts.Length == 0) return null;
        return new PlaceSuggestion(string.Join(", ", parts), coordinates[1].GetDouble(), coordinates[0].GetDouble());
    }
}
