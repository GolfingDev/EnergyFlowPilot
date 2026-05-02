using System.Globalization;
using System.Text.Json;

namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Parses Victron MQTT payloads that either contain a direct number or a JSON object with a value field.
/// </summary>
public static class VictronMqttPayloadParser
{
    public static bool TryParseDecimal(string payload, out decimal value)
    {
        if (decimal.TryParse(payload, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);

            if (document.RootElement.ValueKind == JsonValueKind.Number)
            {
                value = document.RootElement.GetDecimal();
                return true;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("value", out var valueElement) &&
                valueElement.ValueKind == JsonValueKind.Number)
            {
                value = valueElement.GetDecimal();
                return true;
            }
        }
        catch (JsonException)
        {
        }

        value = 0m;
        return false;
    }
}
