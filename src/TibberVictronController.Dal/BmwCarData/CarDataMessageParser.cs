using System.Globalization;
using System.Text.Json;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.BmwCarData;

/// <summary>One descriptor as BMW sent it (diagnostics only).</summary>
public sealed record CarDataRawValue(string Descriptor, string Value, string? Unit, string? Timestamp);

/// <summary>
/// Applies one CarData stream message ({"vin": "...", "data": {"&lt;descriptor&gt;": {"value": ..., "unit": ...,
/// "timestamp": ...}}}) to the car snapshot. A message carries only the descriptors that changed.
/// </summary>
public static class CarDataMessageParser
{
    // Several descriptors report the high-voltage state of charge and differ by about a percent. The newest one is
    // used; at the same measurement time the lower rank wins, so the value the car displays is preferred.
    private static readonly Dictionary<string, int> StateOfChargeDescriptors = new(StringComparer.Ordinal)
    {
        ["vehicle.powertrain.electric.battery.stateOfCharge.displayed"] = 0,
        ["vehicle.drivetrain.batteryManagement.header"] = 1,
        ["vehicle.drivetrain.electricEngine.charging.level"] = 2
    };
    // Value names (keys of CarSnapshot.ValueTimesUtc); the state of charge uses StateOfChargeAtUtc instead.
    private const string TargetField = "target";
    private const string ChargingStatusField = "chargingStatus";
    private const string PluggedField = "plugged";
    private const string RangeField = "range";
    private const string OdometerField = "odometer";
    // Position data stays out of the diagnostic log: it is personal and not needed for charge planning.
    private static readonly string[] LocationMarkers = ["location", "latitude", "longitude", "gps", "heading", "altitude"];
    private const string TargetDescriptor = "vehicle.powertrain.electric.battery.stateOfCharge.target";
    private const string ChargingStatusDescriptor = "vehicle.drivetrain.electricEngine.charging.status";
    // The i4 leaves isPlugged empty (null, never a timestamp); the charging port status carries the plug state
    // (seen 2026-11-05: "CONNECTED"). isPlugged stays as a fallback for other models.
    private const string ChargingPortDescriptor = "vehicle.body.chargingPort.status";
    private const string PluggedDescriptor = "vehicle.powertrain.tractionBattery.charging.port.anyPosition.isPlugged";
    private const string RangeDescriptor = "vehicle.drivetrain.electricEngine.remainingElectricRange";
    private const string OdometerDescriptor = "vehicle.vehicle.travelledDistance";

    /// <summary>Descriptors to select in the BMW portal stream configuration.</summary>
    public static IReadOnlyList<string> RequiredDescriptors { get; } =
    [
        "vehicle.powertrain.electric.battery.stateOfCharge.displayed",
        "vehicle.drivetrain.batteryManagement.header",
        TargetDescriptor,
        ChargingStatusDescriptor,
        ChargingPortDescriptor,
        PluggedDescriptor,
        RangeDescriptor,
        OdometerDescriptor
    ];

    /// <summary>VIN and descriptor names of a message, for diagnostics only (no values).</summary>
    public static (string? Vin, IReadOnlyList<string> Descriptors, string? Problem) Describe(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, [], "Nachricht ist kein JSON-Objekt.");
            var vin = root.TryGetProperty("vin", out var vinElement) && vinElement.ValueKind == JsonValueKind.String ? vinElement.GetString() : null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return (vin, root.EnumerateObject().Select(property => property.Name).ToArray(), "Nachricht ohne Feld 'data'.");
            var descriptors = data.EnumerateObject().Select(property => property.Name).ToArray();
            var known = descriptors.Any(IsEvaluated);
            return (vin, descriptors, known ? null : "Keiner der ausgewerteten Datenpunkte enthalten.");
        }
        catch (JsonException)
        {
            return (null, [], "Nachricht ist kein gueltiges JSON.");
        }
    }

    /// <summary>Returns null when the message is not for the selected car or carries nothing usable.</summary>
    public static CarSnapshot? Apply(CarSnapshot current, string json, string? selectedVin, DateTimeOffset nowUtc)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            var vin = root.TryGetProperty("vin", out var vinElement) && vinElement.ValueKind == JsonValueKind.String ? vinElement.GetString() : null;
            if (selectedVin is not null && vin is not null && !string.Equals(vin, selectedVin, StringComparison.OrdinalIgnoreCase)) return null;

            var next = current with { Vin = vin ?? current.Vin, LastMessageAtUtc = nowUtc };
            var times = new Dictionary<string, DateTimeOffset>(current.ValueTimesUtc ?? new Dictionary<string, DateTimeOffset>(), StringComparer.Ordinal);
            (double Value, DateTimeOffset At, int Rank)? soc = null;
            foreach (var entry in data.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object || !entry.Value.TryGetProperty("value", out var value)) continue;
                var at = Timestamp(entry.Value) ?? nowUtc;
                if (StateOfChargeDescriptors.TryGetValue(entry.Name, out var rank))
                {
                    if (Number(value) is { } candidate and >= 0 and <= 100 && (soc is not { } best || at > best.At || at == best.At && rank < best.Rank))
                        soc = (candidate, at, rank);
                    continue;
                }

                if (entry.Name is ChargingPortDescriptor or PluggedDescriptor)
                {
                    var raw = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
                    raw = raw.Length <= 40 ? raw : raw[..40];
                    if (Plugged(value) is not { } plugged)
                    {
                        // An empty or unknown value must not hide what the other plug descriptor reported.
                        if (next.IsPlugged is null) next = next with { PluggedRawValue = raw };
                        continue;
                    }

                    if (times.TryGetValue(PluggedField, out var knownPlug) && at < knownPlug) continue;
                    next = next with { IsPlugged = plugged, PluggedRawValue = raw };
                    times[PluggedField] = at;
                    continue;
                }

                // A value measured before the one already known is stale (typically from a query) and is skipped.
                if (Field(entry.Name) is not { } field || times.TryGetValue(field, out var known) && at < known) continue;
                if (ApplyValue(next, entry.Name, value) is not { } applied) continue;
                next = applied;
                times[field] = at;
            }

            if (soc is { } newest && (current.StateOfChargeAtUtc is not { } knownAt || newest.At >= knownAt))
                next = next with { StateOfChargePercent = newest.Value, StateOfChargeAtUtc = newest.At };
            return next with { ValueTimesUtc = times };
        }
    }

    /// <summary>
    /// Every descriptor of a message with its raw value, unit and BMW timestamp, for the diagnostic log. Position data
    /// is left out; long values are cut.
    /// </summary>
    public static IReadOnlyList<CarDataRawValue> ReadValues(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object) return [];
            return data.EnumerateObject()
                .Where(entry => !LocationMarkers.Any(marker => entry.Name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                .Select(entry => new CarDataRawValue(
                    entry.Name,
                    Cut(entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("value", out var value)
                        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText()
                        : entry.Value.GetRawText()),
                    Text(entry.Value, "unit"),
                    Text(entry.Value, "timestamp")))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>True for the descriptors this parser turns into car values.</summary>
    public static bool IsEvaluated(string descriptor) => StateOfChargeDescriptors.ContainsKey(descriptor) || Field(descriptor) is not null;

    private static string? Field(string descriptor) => descriptor switch
    {
        TargetDescriptor => TargetField,
        ChargingStatusDescriptor => ChargingStatusField,
        ChargingPortDescriptor or PluggedDescriptor => PluggedField,
        RangeDescriptor => RangeField,
        OdometerDescriptor => OdometerField,
        _ => null
    };

    // Returns null when the value is unusable, so its time is not recorded either.
    private static CarSnapshot? ApplyValue(CarSnapshot next, string descriptor, JsonElement value)
    {
        switch (descriptor)
        {
            case TargetDescriptor when Number(value) is { } target and > 0 and <= 100:
                return next with { TargetStateOfChargePercent = target };
            case ChargingStatusDescriptor when value.ValueKind == JsonValueKind.String:
                return next with { ChargingStatus = value.GetString() };
            case RangeDescriptor when Number(value) is { } range and >= 0:
                return next with { RemainingRangeKm = range };
            case OdometerDescriptor when Number(value) is { } odometer and >= 0:
                return next with { OdometerKm = odometer };
            default:
                return null;
        }
    }

    private static string Cut(string text) => text.Length <= 80 ? text : text[..80] + "…";

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Numbers sometimes arrive as JSON strings.
    private static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var number) => number,
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    // BMW does not document the value format (operator 2026-11-03: plugged in, but nothing was recognised), so any
    // spelling of "(not) plugged/connected" counts; the raw value is kept for diagnosis.
    private static bool? Plugged(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetDouble(out var number) => number != 0,
        JsonValueKind.String => PluggedText(value.GetString()?.Trim().ToUpperInvariant() ?? string.Empty),
        _ => null
    };

    private static bool? PluggedText(string text)
    {
        if (text is "TRUE" or "1" or "YES") return true;
        if (text is "FALSE" or "0" or "NO") return false;
        if (text.Contains("NOT") || text.StartsWith("UN", StringComparison.Ordinal) || text.StartsWith("DIS", StringComparison.Ordinal) ||
            text.StartsWith("NO_", StringComparison.Ordinal)) return false;
        return text.Contains("PLUG") || text.Contains("CONNECT") ? true : null;
    }

    private static DateTimeOffset? Timestamp(JsonElement descriptor) =>
        descriptor.TryGetProperty("timestamp", out var timestamp) && timestamp.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
}
