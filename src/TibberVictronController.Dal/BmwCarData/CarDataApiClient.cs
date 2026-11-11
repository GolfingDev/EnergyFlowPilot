using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TibberVictronController.Dal.BmwCarData;

/// <summary>BMW refused a request because the daily quota (about 50 calls) is used up.</summary>
public sealed class CarDataQuotaExceededException(string message) : Exception(message);

/// <summary>
/// BMW CarData REST API (read-only): vehicle mappings, the controller's own data container and the last known values
/// of its descriptors. Used when the stream stays silent; every call counts against BMW's daily quota.
/// </summary>
public sealed class CarDataApiClient(HttpClient httpClient)
{
    public const string BaseUrl = "https://api-cardata.bmwgroup.com";
    // A new name when the descriptor list changes makes the controller create a fitting new container.
    public const string ContainerName = "EnergyFlowPilot v3";
    private const string ContainerPurpose = "Ladeplanung des Autos";

    /// <summary>VIN of the car the account is primary user of (CarData data access needs that role).</summary>
    public async Task<string?> GetPrimaryVinAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "/customers/vehicles/mappings", accessToken, null, cancellationToken);
        var items = Items(document.RootElement, "mappings");
        var primary = items.FirstOrDefault(item => Text(item, "mappingType") == "PRIMARY");
        return Text(primary.ValueKind == JsonValueKind.Object ? primary : items.FirstOrDefault(), "vin");
    }

    /// <summary>Returns the id of the controller's container, creating it with the given descriptors if it is missing.</summary>
    public async Task<string> EnsureContainerAsync(string accessToken, IReadOnlyList<string> descriptors, CancellationToken cancellationToken)
    {
        using (var list = await SendAsync(HttpMethod.Get, "/customers/containers", accessToken, null, cancellationToken))
        {
            var existing = Items(list.RootElement, "containers").FirstOrDefault(item => Text(item, "name") == ContainerName);
            if (Text(existing, "containerId") is { } id) return id;
        }

        using var created = await SendAsync(HttpMethod.Post, "/customers/containers", accessToken,
            new { name = ContainerName, purpose = ContainerPurpose, technicalDescriptors = descriptors }, cancellationToken);
        return Text(created.RootElement, "containerId")
            ?? throw new InvalidOperationException("BMW hat den Daten-Container nicht angelegt (keine containerId in der Antwort).");
    }

    /// <summary>
    /// Last known values as a stream-shaped message ({"vin": ..., "data": {descriptor: {value, unit, timestamp}}}),
    /// so the same parser handles stream and query.
    /// </summary>
    public async Task<string> GetTelematicDataAsync(string accessToken, string vin, string containerId, CancellationToken cancellationToken)
    {
        var path = $"/customers/vehicles/{Uri.EscapeDataString(vin)}/telematicData?containerId={Uri.EscapeDataString(containerId)}";
        using var document = await SendAsync(HttpMethod.Get, path, accessToken, null, cancellationToken);
        var root = document.RootElement;
        var data = root.TryGetProperty("telematicData", out var telematic) ? telematic
            : root.TryGetProperty("data", out var fallback) ? fallback : default;
        if (data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("BMW liefert keine Fahrzeugwerte (Feld 'telematicData' fehlt).");
        return JsonSerializer.Serialize(new { vin, data });
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string accessToken, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("x-version", "v1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new CarDataQuotaExceededException("BMW meldet: Tageskontingent für Abfragen aufgebraucht (HTTP 429).");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? $"BMW verweigert die Abfrage (HTTP {(int)response.StatusCode}). Ist der Client für die CarData API abonniert und das Auto als Hauptnutzer zugeordnet?"
                : $"BMW-Abfrage fehlgeschlagen (HTTP {(int)response.StatusCode}).");
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    // Lists arrive either bare or wrapped in an object.
    private static JsonElement[] Items(JsonElement root, string key) => root.ValueKind switch
    {
        JsonValueKind.Array => root.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray(),
        JsonValueKind.Object when root.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array =>
            list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray(),
        _ => []
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
