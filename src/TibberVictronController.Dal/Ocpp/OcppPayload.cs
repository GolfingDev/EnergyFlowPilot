using System.Globalization;
using System.Text.Json;

namespace TibberVictronController.Dal.Ocpp;

/// <summary>A CALL whose payload violates the OCPP 1.6 schema; answered with a CALLERROR instead of a result.</summary>
public sealed class OcppPayloadException(string message) : FormatException(message);

/// <summary>Strict readers for OCPP payload fields, with German errors that name the field.</summary>
internal static class OcppPayload
{
    public static string Text(JsonElement payload, string name) =>
        OptionalText(payload, name) ?? throw new OcppPayloadException($"Das Pflichtfeld '{name}' fehlt.");

    public static string? OptionalText(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static int Int(JsonElement payload, string name) =>
        OptionalInt(payload, name) ?? throw new OcppPayloadException($"Das Pflichtfeld '{name}' fehlt oder ist keine Ganzzahl.");

    public static int? OptionalInt(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    public static double Number(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : throw new OcppPayloadException($"Das Pflichtfeld '{name}' fehlt oder ist keine Zahl.");

    public static DateTimeOffset? OptionalTimestamp(JsonElement payload, string name) =>
        OptionalText(payload, name) is { } text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    /// <summary>
    /// The wallbox clock can be wrong (e.g. 1970 after a power cut before time sync). Its timestamp is used only when
    /// it is plausible; otherwise the controller's own receive time counts.
    /// </summary>
    public static DateTimeOffset PlausibleTimestamp(JsonElement payload, string name, DateTimeOffset nowUtc) =>
        OptionalTimestamp(payload, name) is { } reported && (reported - nowUtc).Duration() <= TimeSpan.FromDays(1)
            ? reported
            : nowUtc;
}
