using System.Globalization;
using System.Text.Json;

namespace TibberVictronController.Dal.Ocpp;

public enum OcppMessageType
{
    Call = 2,
    CallResult = 3,
    CallError = 4
}

/// <summary>
/// One OCPP-J 1.6 message: CALL [2, id, action, payload], CALLRESULT [3, id, payload] or
/// CALLERROR [4, id, code, description, details].
/// </summary>
public sealed record OcppFrame(
    OcppMessageType Type,
    string UniqueId,
    string? Action,
    JsonElement Payload,
    string? ErrorCode,
    string? ErrorDescription)
{
    private const int MaximumUniqueIdLength = 36;

    public static OcppFrame Parse(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new FormatException("Die OCPP-Nachricht ist kein gueltiges JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3)
                throw new FormatException("Die OCPP-Nachricht ist kein gueltiges Nachrichtenfeld.");
            if (root[0].ValueKind != JsonValueKind.Number || !root[0].TryGetInt32(out var typeId) || typeId is < 2 or > 4)
                throw new FormatException("Unbekannter OCPP-Nachrichtentyp.");
            var uniqueId = ReadUniqueId(root);
            var type = (OcppMessageType)typeId;
            return type switch
            {
                OcppMessageType.Call when root.GetArrayLength() >= 4 && root[2].ValueKind == JsonValueKind.String =>
                    new OcppFrame(type, uniqueId, root[2].GetString(), RequireObject(root[3]), null, null),
                OcppMessageType.CallResult =>
                    new OcppFrame(type, uniqueId, null, RequireObject(root[2]), null, null),
                OcppMessageType.CallError when root.GetArrayLength() >= 4 =>
                    new OcppFrame(type, uniqueId, null, root.GetArrayLength() >= 5 ? root[4].Clone() : default,
                        root[2].ValueKind == JsonValueKind.String ? root[2].GetString() : null,
                        root[3].ValueKind == JsonValueKind.String ? root[3].GetString() : null),
                _ => throw new FormatException("Die OCPP-Nachricht ist unvollstaendig.")
            };
        }
    }

    /// <summary>Best effort: lets a malformed CALL still be answered with a CALLERROR for its id.</summary>
    public static string? TryReadUniqueId(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 2 &&
                   root[0].ValueKind == JsonValueKind.Number && root[0].TryGetInt32(out var type) && type == (int)OcppMessageType.Call
                ? ReadUniqueId(root)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }
    }

    public static string Call(string uniqueId, string action, object payload) =>
        JsonSerializer.Serialize(new object[] { (int)OcppMessageType.Call, uniqueId, action, payload });

    public static string Result(string uniqueId, object payload) =>
        JsonSerializer.Serialize(new object[] { (int)OcppMessageType.CallResult, uniqueId, payload });

    public static string Error(string uniqueId, string errorCode, string description) =>
        JsonSerializer.Serialize(new object[] { (int)OcppMessageType.CallError, uniqueId, errorCode, description, new { } });

    /// <summary>OCPP expects RFC 3339 timestamps; "Z" is the form every charger understands.</summary>
    public static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string ReadUniqueId(JsonElement root)
    {
        var id = root[1].ValueKind == JsonValueKind.String ? root[1].GetString() : null;
        if (string.IsNullOrEmpty(id) || id.Length > MaximumUniqueIdLength)
            throw new FormatException("Die OCPP-Nachrichten-ID fehlt oder ist zu lang.");
        return id;
    }

    private static JsonElement RequireObject(JsonElement element) => element.ValueKind == JsonValueKind.Object
        ? element.Clone()
        : throw new FormatException("Die OCPP-Nutzdaten muessen ein JSON-Objekt sein.");
}
