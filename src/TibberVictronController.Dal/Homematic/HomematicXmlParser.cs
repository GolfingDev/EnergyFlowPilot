using System.Globalization;
using System.Xml.Linq;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Homematic;

/// <summary>
/// Turns the XML-API statelist/devicelist pair into climate samples.
/// Selection works on an explicit device type allow-list because generic datapoint names are
/// ambiguous: switch actuators report ACTUAL_TEMPERATURE as device temperature on channel 0,
/// and blinds or dimmers report LEVEL as position.
/// </summary>
public static class HomematicXmlParser
{
    private const string BidCosRadiatorThermostatType = "HM-CC-RT-DN";

    public static HomematicClimateSnapshot Parse(XDocument stateList, XDocument deviceList, DateTimeOffset polledAtUtc)
    {
        EnsureAuthenticated(stateList);
        EnsureAuthenticated(deviceList);

        var deviceInfoByIseId = ReadDeviceInfos(deviceList);
        var samples = new List<HomematicClimateSample>();
        var ignoredClimateDeviceTypes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in stateList.Root!.Elements("device"))
        {
            if (!deviceInfoByIseId.TryGetValue(ReadAttribute(device, "ise_id"), out var deviceInfo))
            {
                continue;
            }

            // Channel 0 is the maintenance channel; its temperature is the device's own temperature.
            var channels = device.Elements("channel")
                .Where(channel => ReadAttribute(channel, "index") != "0")
                .Select(channel => (Channel: channel, Datapoints: ReadDatapoints(channel)))
                .ToArray();
            var kind = Classify(deviceInfo.DeviceType);

            if (kind is null)
            {
                if (channels.Any(channel => LooksClimateRelated(channel.Datapoints)))
                {
                    ignoredClimateDeviceTypes.Add(deviceInfo.DeviceType);
                }

                continue;
            }

            var isReachable = !string.Equals(ReadAttribute(device, "unreach"), "true", StringComparison.OrdinalIgnoreCase);

            foreach (var (channel, datapoints) in channels)
            {
                // Underfloor zones have no own temperature; unused actuator outputs report an empty LEVEL.
                var primaryDatapointType = kind == HomematicClimateSampleKind.UnderfloorHeatingZone
                    ? "LEVEL"
                    : "ACTUAL_TEMPERATURE";

                if (ReadDecimal(datapoints, primaryDatapointType) is null)
                {
                    continue;
                }

                samples.Add(new HomematicClimateSample
                {
                    PolledAtUtc = polledAtUtc,
                    LastChangedAtUtc = ReadTimestamp(datapoints, primaryDatapointType),
                    ChannelAddress = $"{deviceInfo.Address}:{ReadAttribute(channel, "index")}",
                    DeviceType = deviceInfo.DeviceType,
                    DeviceName = ReadAttribute(device, "name"),
                    ChannelName = ReadAttribute(channel, "name"),
                    Kind = kind.Value,
                    ActualTemperatureCelsius = ReadDecimal(datapoints, "ACTUAL_TEMPERATURE"),
                    // BidCos devices use SET_TEMPERATURE/ACTUAL_HUMIDITY instead of the HmIP names.
                    SetPointTemperatureCelsius = ReadDecimal(datapoints, "SET_POINT_TEMPERATURE") ?? ReadDecimal(datapoints, "SET_TEMPERATURE"),
                    SetPointMode = ReadSetPointMode(datapoints),
                    ValveOpeningPercent = ReadValveOpeningPercent(deviceInfo.DeviceType, datapoints),
                    RelativeHumidityPercent = ReadDecimal(datapoints, "HUMIDITY") ?? ReadDecimal(datapoints, "ACTUAL_HUMIDITY"),
                    IsWindowOpen = ReadInteger(datapoints, "WINDOW_STATE") switch
                    {
                        0 => false,
                        1 => true,
                        _ => null
                    },
                    IsReachable = isReachable
                });
            }
        }

        return new HomematicClimateSnapshot
        {
            Samples = samples,
            IgnoredClimateDeviceTypes = ignoredClimateDeviceTypes.ToArray()
        };
    }

    private static void EnsureAuthenticated(XDocument document)
    {
        if (document.Root is null)
        {
            throw new HomematicApiException("Die Homematic-XML-API hat eine leere Antwort geliefert.");
        }

        // The XML-API reports a missing or invalid token with HTTP 200 and this marker element.
        if (document.Root.Element("not_authenticated") is not null)
        {
            throw new HomematicApiException("Die Homematic-XML-API hat den Zugriff verweigert. Bitte den API-Token pruefen.");
        }
    }

    private static Dictionary<string, DeviceInfo> ReadDeviceInfos(XDocument deviceList)
    {
        var deviceInfoByIseId = new Dictionary<string, DeviceInfo>(StringComparer.Ordinal);

        foreach (var device in deviceList.Root!.Elements("device"))
        {
            deviceInfoByIseId.TryAdd(
                ReadAttribute(device, "ise_id"),
                new DeviceInfo(ReadAttribute(device, "device_type"), ReadAttribute(device, "address")));
        }

        return deviceInfoByIseId;
    }

    private static HomematicClimateSampleKind? Classify(string deviceType)
    {
        if (deviceType.Equals("HmIP-HEATING", StringComparison.OrdinalIgnoreCase))
        {
            return HomematicClimateSampleKind.HeatingGroup;
        }

        if (deviceType.StartsWith("HmIP-eTRV", StringComparison.OrdinalIgnoreCase) ||
            deviceType.Equals(BidCosRadiatorThermostatType, StringComparison.OrdinalIgnoreCase))
        {
            return HomematicClimateSampleKind.RadiatorThermostat;
        }

        if (deviceType.StartsWith("HmIP-WTH", StringComparison.OrdinalIgnoreCase) ||
            deviceType.StartsWith("HmIP-BWTH", StringComparison.OrdinalIgnoreCase) ||
            deviceType.Equals("HM-TC-IT-WM-W-EU", StringComparison.OrdinalIgnoreCase))
        {
            return HomematicClimateSampleKind.WallThermostat;
        }

        if (deviceType.StartsWith("HmIP-FAL", StringComparison.OrdinalIgnoreCase))
        {
            return HomematicClimateSampleKind.UnderfloorHeatingZone;
        }

        if (deviceType.StartsWith("HmIP-SWO", StringComparison.OrdinalIgnoreCase))
        {
            return HomematicClimateSampleKind.WeatherStation;
        }

        return null;
    }

    private static bool LooksClimateRelated(IReadOnlyDictionary<string, XElement> datapoints)
    {
        return ReadDecimal(datapoints, "ACTUAL_TEMPERATURE") is not null ||
            ReadDecimal(datapoints, "SET_POINT_TEMPERATURE") is not null ||
            ReadDecimal(datapoints, "SET_TEMPERATURE") is not null;
    }

    private static decimal? ReadValveOpeningPercent(string deviceType, IReadOnlyDictionary<string, XElement> datapoints)
    {
        // Only the BidCos radiator thermostat reports VALVE_STATE as percent.
        // On HmIP devices VALVE_STATE is a status code and LEVEL holds the 0-1 opening.
        if (deviceType.Equals(BidCosRadiatorThermostatType, StringComparison.OrdinalIgnoreCase))
        {
            return ReadDecimal(datapoints, "VALVE_STATE");
        }

        return ReadDecimal(datapoints, "LEVEL") * 100m;
    }

    private static HomematicSetPointMode? ReadSetPointMode(IReadOnlyDictionary<string, XElement> datapoints)
    {
        if (datapoints.TryGetValue("BOOST_MODE", out var boostMode) &&
            string.Equals((string?)boostMode.Attribute("value"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return HomematicSetPointMode.Boost;
        }

        // HmIP SET_POINT_MODE and BidCos CONTROL_MODE share the same numbering for 0-2; BidCos adds 3 = boost.
        return (ReadInteger(datapoints, "SET_POINT_MODE") ?? ReadInteger(datapoints, "CONTROL_MODE")) switch
        {
            0 => HomematicSetPointMode.Automatic,
            1 => HomematicSetPointMode.Manual,
            2 => HomematicSetPointMode.Party,
            3 => HomematicSetPointMode.Boost,
            _ => null
        };
    }

    private static Dictionary<string, XElement> ReadDatapoints(XElement channel)
    {
        var datapoints = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var datapoint in channel.Elements("datapoint"))
        {
            datapoints.TryAdd(ReadAttribute(datapoint, "type"), datapoint);
        }

        return datapoints;
    }

    private static decimal? ReadDecimal(IReadOnlyDictionary<string, XElement> datapoints, string type)
    {
        return datapoints.TryGetValue(type, out var datapoint) &&
            decimal.TryParse((string?)datapoint.Attribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    private static int? ReadInteger(IReadOnlyDictionary<string, XElement> datapoints, string type)
    {
        return datapoints.TryGetValue(type, out var datapoint) &&
            int.TryParse((string?)datapoint.Attribute("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    private static DateTimeOffset? ReadTimestamp(IReadOnlyDictionary<string, XElement> datapoints, string type)
    {
        return datapoints.TryGetValue(type, out var datapoint) &&
            long.TryParse((string?)datapoint.Attribute("timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds) &&
            unixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                : null;
    }

    private static string ReadAttribute(XElement element, string name)
    {
        return (string?)element.Attribute(name) ?? string.Empty;
    }

    private sealed record DeviceInfo(string DeviceType, string Address);
}
