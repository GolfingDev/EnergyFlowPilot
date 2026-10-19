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

    public static readonly IReadOnlySet<string> BatteryPowerAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "batteryPowerWatts",
        "batteryPower",
        "batteryChargePower",
        "batteryDischargePower",
        "storageChargePower",
        "storageDischargePower",
        "storagePower"
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

    /// <summary>
    /// Returns the production of PV inverters connected to the E3/DC through an additional meter ("Ext. PV" in the app).
    /// </summary>
    /// <remarks>
    /// On this site the field <c>externalProduction</c> stays 0 while the external inverter reports through
    /// <c>meters[]</c> with <c>meterType = production</c> and a negative power (2026-10-19: pvProduction 664 W, production
    /// meter -3536 W, consumption 1122 W = 664 + 3536 - 2916 battery charge - 162 export). The larger of both sources is
    /// used, so the value is not counted twice should the API fill the field one day.
    /// </remarks>
    public static decimal GetExternalProductionWatts(JsonElement root)
    {
        var externalField = TryFindDecimalByPropertyName(root, ExternalProductionAliases, out var fieldValue)
            ? Math.Abs(fieldValue)
            : 0m;
        var productionMeters = 0m;

        if (TryFindProperty(root, "meters", out var meters) && meters.ValueKind == JsonValueKind.Array)
        {
            foreach (var meter in meters.EnumerateArray())
            {
                if (meter.ValueKind == JsonValueKind.Object &&
                    meter.TryGetProperty("meterType", out var meterType) &&
                    string.Equals(meterType.GetString(), "production", StringComparison.OrdinalIgnoreCase) &&
                    meter.TryGetProperty("power", out var power) &&
                    TryReadDecimal(power, out var powerWatts))
                {
                    productionMeters += Math.Abs(powerWatts);
                }
            }
        }

        return Math.Max(externalField, productionMeters);
    }

    private static readonly IReadOnlySet<string> ExternalProductionAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "externalProduction"
    };

    private static bool TryFindProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }

                if (TryFindProperty(property.Value, name, out value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static decimal? TryGetDecimal(JsonElement root, IReadOnlySet<string> aliases)
    {
        return TryFindDecimalByPropertyName(root, aliases, out var value) ? value : null;
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
