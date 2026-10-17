using System.Xml.Linq;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Homematic;

namespace TibberVictronController.Dal.Tests;

/// <summary>
/// Uses trimmed real responses of the OpenCCU XML-API (ISO-8859-1) captured on 2026-10-17.
/// </summary>
public sealed class HomematicXmlParserTests
{
    private static readonly DateTimeOffset PolledAtUtc = new(2026, 10, 17, 10, 40, 0, TimeSpan.Zero);

    [Fact]
    public void ParseReadsAllSupportedClimateChannelsAndSkipsOtherDevices()
    {
        var snapshot = HomematicXmlParser.Parse(LoadStateList(), LoadDeviceList(), PolledAtUtc);

        // 1 group + 1 HmIP radiator + 1 BidCos radiator + 2 wall thermostats + 7 underfloor zones + 1 weather station.
        Assert.Equal(13, snapshot.Samples.Count);
        Assert.All(snapshot.Samples, sample => Assert.Equal(PolledAtUtc, sample.PolledAtUtc));
        Assert.DoesNotContain(snapshot.Samples, sample => sample.DeviceType is "HmIP-FSM" or "HM-LC-Bl1PBU-FM");
        Assert.DoesNotContain(snapshot.Samples, sample => sample.ChannelAddress.EndsWith(":0", StringComparison.Ordinal));
        Assert.Empty(snapshot.IgnoredClimateDeviceTypes);
    }

    [Fact]
    public void ParseReadsHeatingGroupWithValveOpeningFromLevel()
    {
        var sample = ParseSingle("INT0000003:1");

        Assert.Equal(HomematicClimateSampleKind.HeatingGroup, sample.Kind);
        Assert.Equal("Heizung Bad INT0000003", sample.DeviceName);
        Assert.Equal(21.5m, sample.ActualTemperatureCelsius);
        Assert.Equal(22m, sample.SetPointTemperatureCelsius);
        Assert.Equal(HomematicSetPointMode.Automatic, sample.SetPointMode);
        Assert.Equal(89m, sample.ValveOpeningPercent);
        Assert.Equal(51m, sample.RelativeHumidityPercent);
        Assert.False(sample.IsWindowOpen);
        Assert.True(sample.IsReachable);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792233254), sample.LastChangedAtUtc);
    }

    [Fact]
    public void ParseIgnoresHmIpValveStateStatusCodeForRadiatorThermostat()
    {
        // HmIP VALVE_STATE = 4 is a status code; the real opening is LEVEL = 0.89.
        var sample = ParseSingle("0050226997F35A:1");

        Assert.Equal(HomematicClimateSampleKind.RadiatorThermostat, sample.Kind);
        Assert.Equal(21.4m, sample.ActualTemperatureCelsius);
        Assert.Equal(89m, sample.ValveOpeningPercent);
    }

    [Fact]
    public void ParseReadsBidCosRadiatorThermostatWithLegacyDatapointNames()
    {
        var sample = ParseSingle("OEQ2086190:4");

        Assert.Equal(HomematicClimateSampleKind.RadiatorThermostat, sample.Kind);
        Assert.Equal("Heizung Büro", sample.DeviceName);
        Assert.Equal(20.7m, sample.ActualTemperatureCelsius);
        Assert.Equal(19m, sample.SetPointTemperatureCelsius);
        Assert.Equal(HomematicSetPointMode.Automatic, sample.SetPointMode);
        Assert.Equal(0m, sample.ValveOpeningPercent);
        Assert.Null(sample.RelativeHumidityPercent);
        Assert.Null(sample.IsWindowOpen);
    }

    [Fact]
    public void ParseReadsBidCosWallThermostatHumidityFromActualHumidity()
    {
        var sample = ParseSingle("OEQ0849796:2");

        Assert.Equal(HomematicClimateSampleKind.WallThermostat, sample.Kind);
        Assert.Equal(18.3m, sample.ActualTemperatureCelsius);
        Assert.Equal(17m, sample.SetPointTemperatureCelsius);
        Assert.Equal(57m, sample.RelativeHumidityPercent);
        Assert.Null(sample.ValveOpeningPercent);
    }

    [Fact]
    public void ParseReadsOnlyUsedUnderfloorHeatingZones()
    {
        var snapshot = HomematicXmlParser.Parse(LoadStateList(), LoadDeviceList(), PolledAtUtc);
        var zones = snapshot.Samples
            .Where(sample => sample.Kind == HomematicClimateSampleKind.UnderfloorHeatingZone)
            .ToArray();

        Assert.Equal(
            new[] { "Esszimmer", "Flur", "Bad WC Eingang", "Küche", "Wohnzimmer Tür", "Wohnzimmer Mitte", "Wohnzimmer Fenster" },
            zones.Select(zone => zone.ChannelName));
        var bathZone = Assert.Single(zones, zone => zone.ChannelAddress == "001BA0C9AE35FA:6");
        Assert.Equal(86.5m, bathZone.ValveOpeningPercent);
        Assert.Null(bathZone.ActualTemperatureCelsius);
        Assert.Null(bathZone.SetPointMode);
    }

    [Fact]
    public void ParseReadsWeatherStationAsOutdoorReference()
    {
        var sample = ParseSingle("0017DD89A019BA:1");

        Assert.Equal(HomematicClimateSampleKind.WeatherStation, sample.Kind);
        Assert.Equal(10.1m, sample.ActualTemperatureCelsius);
        Assert.Equal(94m, sample.RelativeHumidityPercent);
        Assert.Null(sample.SetPointTemperatureCelsius);
    }

    [Fact]
    public void ParseMarksChannelsOfUnreachableDeviceAsUnreachable()
    {
        var stateList = LoadStateList();
        FindStateDevice(stateList, "Heizung Bad").SetAttributeValue("unreach", "true");

        var snapshot = HomematicXmlParser.Parse(stateList, LoadDeviceList(), PolledAtUtc);

        Assert.False(Assert.Single(snapshot.Samples, sample => sample.ChannelAddress == "0050226997F35A:1").IsReachable);
        Assert.True(Assert.Single(snapshot.Samples, sample => sample.ChannelAddress == "INT0000003:1").IsReachable);
    }

    [Fact]
    public void ParseLeavesMissingOrEmptyOptionalDatapointsEmpty()
    {
        var stateList = LoadStateList();
        var groupDatapoints = FindStateDevice(stateList, "Heizung Bad INT0000003").Descendants("datapoint").ToArray();
        groupDatapoints.Single(datapoint => (string?)datapoint.Attribute("type") == "SET_POINT_TEMPERATURE").Remove();
        groupDatapoints.Single(datapoint => (string?)datapoint.Attribute("type") == "HUMIDITY").SetAttributeValue("value", "");
        groupDatapoints.Single(datapoint => (string?)datapoint.Attribute("type") == "ACTUAL_TEMPERATURE").SetAttributeValue("timestamp", "0");

        var sample = Assert.Single(
            HomematicXmlParser.Parse(stateList, LoadDeviceList(), PolledAtUtc).Samples,
            sample => sample.ChannelAddress == "INT0000003:1");

        Assert.Equal(21.5m, sample.ActualTemperatureCelsius);
        Assert.Null(sample.SetPointTemperatureCelsius);
        Assert.Null(sample.RelativeHumidityPercent);
        Assert.Null(sample.LastChangedAtUtc);
    }

    [Fact]
    public void ParseReportsUnsupportedClimateDeviceTypesInsteadOfStoringThem()
    {
        var deviceList = LoadDeviceList();
        deviceList.Root!.Elements("device")
            .Single(device => (string?)device.Attribute("name") == "Thermostat Bad")
            .SetAttributeValue("device_type", "HmIP-STH");

        var snapshot = HomematicXmlParser.Parse(LoadStateList(), deviceList, PolledAtUtc);

        Assert.Equal(12, snapshot.Samples.Count);
        Assert.Equal(new[] { "HmIP-STH" }, snapshot.IgnoredClimateDeviceTypes);
    }

    [Fact]
    public void ParseThrowsWhenXmlApiRejectsToken()
    {
        var notAuthenticated = XDocument.Load(GetTestDataPath("statelist-not-authenticated.xml"));

        var exception = Assert.Throws<HomematicApiException>(() =>
            HomematicXmlParser.Parse(notAuthenticated, LoadDeviceList(), PolledAtUtc));

        Assert.Contains("Token", exception.Message, StringComparison.Ordinal);
    }

    internal static string GetTestDataPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "Homematic", fileName);
    }

    private static HomematicClimateSample ParseSingle(string channelAddress)
    {
        var snapshot = HomematicXmlParser.Parse(LoadStateList(), LoadDeviceList(), PolledAtUtc);

        return Assert.Single(snapshot.Samples, sample => sample.ChannelAddress == channelAddress);
    }

    private static XElement FindStateDevice(XDocument stateList, string name)
    {
        return stateList.Root!.Elements("device").Single(device => (string?)device.Attribute("name") == name);
    }

    private static XDocument LoadStateList() => XDocument.Load(GetTestDataPath("statelist.xml"));

    private static XDocument LoadDeviceList() => XDocument.Load(GetTestDataPath("devicelist.xml"));
}
