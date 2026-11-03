using System.Globalization;
using System.Text.Json;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.BmwCarData;

/// <summary>
/// Applies one CarData stream message ({"vin": "...", "data": {"&lt;descriptor&gt;": {"value": ..., "unit": ...,
/// "timestamp": ...}}}) to the car snapshot. A message carries only the descriptors that changed.
/// </summary>
public static class CarDataMessageParser
{
    // Several descriptors report the high-voltage state of charge; whichever arrives is used.
    private static readonly HashSet<string> StateOfChargeDescriptors = new(StringComparer.Ordinal)
    {
        "vehicle.powertrain.electric.battery.stateOfCharge.displayed",
        "vehicle.drivetrain.batteryManagement.header",
        "vehicle.drivetrain.electricEngine.charging.level"
    };
    private const string TargetDescriptor = "vehicle.powertrain.electric.battery.stateOfCharge.target";
    private const string ChargingStatusDescriptor = "vehicle.drivetrain.electricEngine.charging.status";
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
            var known = descriptors.Any(name => StateOfChargeDescriptors.Contains(name) || name is TargetDescriptor or ChargingStatusDescriptor
                or PluggedDescriptor or RangeDescriptor or OdometerDescriptor);
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
            foreach (var entry in data.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object || !entry.Value.TryGetProperty("value", out var value)) continue;
                if (StateOfChargeDescriptors.Contains(entry.Name) && Number(value) is { } soc and >= 0 and <= 100)
                    next = next with { StateOfChargePercent = soc, StateOfChargeAtUtc = Timestamp(entry.Value) ?? nowUtc };
                else if (entry.Name == TargetDescriptor && Number(value) is { } target and > 0 and <= 100)
                    next = next with { TargetStateOfChargePercent = target };
                else if (entry.Name == ChargingStatusDescriptor && value.ValueKind == JsonValueKind.String)
                    next = next with { ChargingStatus = value.GetString() };
                else if (entry.Name == PluggedDescriptor)
                {
                    var raw = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
                    next = next with { PluggedRawValue = raw.Length <= 40 ? raw : raw[..40] };
                    if (Plugged(value) is { } plugged) next = next with { IsPlugged = plugged };
                }
                else if (entry.Name == RangeDescriptor && Number(value) is { } range and >= 0)
                    next = next with { RemainingRangeKm = range };
                else if (entry.Name == OdometerDescriptor && Number(value) is { } odometer and >= 0)
                    next = next with { OdometerKm = odometer };
            }

            return next;
        }
    }

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
