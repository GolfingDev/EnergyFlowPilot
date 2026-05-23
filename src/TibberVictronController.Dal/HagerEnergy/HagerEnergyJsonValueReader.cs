using System.Globalization;
using System.Text.Json;

namespace TibberVictronController.Dal.HagerEnergy;

internal static class HagerEnergyJsonValueReader
{
    public static readonly IReadOnlySet<string> GridImportAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gridImportWatts",
        "gridImportPower",
        "gridPowerConsumption",
        "gridConsumption",
        "gridPower",
        "gridSupply",
        "powerFromGrid"
    };

    public static readonly IReadOnlySet<string> PvProductionAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pvProductionWatts",
        "pvPower",
        "pvProduction",
        "photovoltaicPower",
        "solarPower",
        "productionPower"
    };

    public static readonly IReadOnlySet<string> BatterySocAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "batterySocPercent",
        "batterySoc",
        "batteryStateOfCharge",
        "stateOfCharge",
        "stateOfChargePercent",
        "soc"
    };

    public static decimal GetRequiredDecimal(
        JsonElement root,
        string configuredPath,
        IReadOnlySet<string> aliases,
        string displayName)
    {
        if (TryReadDecimalAtPath(root, configuredPath, out var configuredValue))
        {
            return configuredValue;
        }

        if (TryFindDecimalByPropertyName(root, aliases, out var discoveredValue))
        {
            return discoveredValue;
        }

        throw new HagerEnergyApiException($"Die Hager-Energy-Antwort enthaelt keinen verwendbaren Wert fuer {displayName}. Bitte JSON-Pfad in den Einstellungen anpassen.");
    }

    private static bool TryReadDecimalAtPath(JsonElement root, string path, out decimal value)
    {
        value = 0m;
        var current = root;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var arrayIndex))
            {
                if (arrayIndex < 0 || arrayIndex >= current.GetArrayLength())
                {
                    return false;
                }

                current = current[arrayIndex];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        return TryReadDecimal(current, out value);
    }

    private static bool TryFindDecimalByPropertyName(JsonElement element, IReadOnlySet<string> aliases, out decimal value)
    {
        value = 0m;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (aliases.Contains(property.Name) && TryReadDecimal(property.Value, out value))
                {
                    return true;
                }

                if (TryFindDecimalByPropertyName(property.Value, aliases, out value))
                {
                    return true;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindDecimalByPropertyName(item, aliases, out value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        value = 0m;

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDecimal(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var rawValue = element.GetString();

            return decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("value", out var valueElement))
        {
            return TryReadDecimal(valueElement, out value);
        }

        return false;
    }
}
