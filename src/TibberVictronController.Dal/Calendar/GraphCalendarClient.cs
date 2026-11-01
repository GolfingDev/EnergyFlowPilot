using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Calendar;

public sealed record GraphCredentials(string TenantId, string ClientId, string ClientSecret);

/// <summary>
/// Reads appointments through Microsoft Graph with the application permission Calendars.Read (client credentials of
/// the existing Entra app). Times are requested in UTC; only the fields needed for trips are selected.
/// </summary>
public sealed class GraphCalendarClient(HttpClient httpClient)
{
    public const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private const int MaximumPages = 20;

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(GraphCredentials credentials, string mailbox,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(credentials, cancellationToken);
        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/calendarView" +
                  $"?startDateTime={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture))}" +
                  $"&endDateTime={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture))}" +
                  "&$select=id,subject,location,start,end,isAllDay,isCancelled,isOnlineMeeting&$top=100&$orderby=start/dateTime";
        var events = new List<CalendarEvent>();
        for (var page = 0; url is not null && page < MaximumPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(GraphError(response.StatusCode, mailbox));
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
                events.AddRange(values.EnumerateArray().Select(Parse).OfType<CalendarEvent>());
            url = root.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }

        return events;
    }

    private async Task<string> GetTokenAsync(GraphCredentials credentials, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(credentials.TenantId)}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = credentials.ClientId,
                ["client_secret"] = credentials.ClientSecret,
                ["scope"] = "https://graph.microsoft.com/.default"
            }), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = document.RootElement;
        if (response.IsSuccessStatusCode && root.TryGetProperty("access_token", out var token) && token.GetString() is { } value)
            return value;
        // Only the error code is reported: the response must never put secrets into logs.
        var code = root.TryGetProperty("error", out var error) ? error.GetString() : null;
        throw new InvalidOperationException($"Anmeldung bei Microsoft Graph fehlgeschlagen (HTTP {(int)response.StatusCode}{(code is null ? string.Empty : ", " + code)}). Client-Secret und Mandant pruefen.");
    }

    private static CalendarEvent? Parse(JsonElement item)
    {
        var id = Text(item, "id");
        if (id is null || !item.TryGetProperty("start", out var start) || !item.TryGetProperty("end", out var end)) return null;
        if (Time(start) is not { } startUtc || Time(end) is not { } endUtc) return null;
        string? location = null;
        if (item.TryGetProperty("location", out var place) && place.ValueKind == JsonValueKind.Object)
        {
            location = Text(place, "displayName");
            // A structured address is more precise than a room name; use it when the display name is empty.
            if (string.IsNullOrWhiteSpace(location) && place.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.Object)
                location = string.Join(", ", new[] { Text(address, "street"), Text(address, "postalCode"), Text(address, "city") }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        return new CalendarEvent(id, Text(item, "subject") ?? string.Empty, string.IsNullOrWhiteSpace(location) ? null : location,
            startUtc, endUtc, Bool(item, "isAllDay"), Bool(item, "isOnlineMeeting"), Bool(item, "isCancelled"));
    }

    private static DateTimeOffset? Time(JsonElement value) =>
        Text(value, "dateTime") is { } text &&
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc))
            : null;

    private static string GraphError(HttpStatusCode status, string mailbox) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            $"Microsoft Graph verweigert den Kalenderzugriff (HTTP {(int)status}). Anwendungsberechtigung Calendars.Read mit Administratorzustimmung pruefen.",
        HttpStatusCode.NotFound => $"Das Postfach {mailbox} wurde in Microsoft 365 nicht gefunden.",
        _ => $"Kalender konnte nicht gelesen werden (HTTP {(int)status})."
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
