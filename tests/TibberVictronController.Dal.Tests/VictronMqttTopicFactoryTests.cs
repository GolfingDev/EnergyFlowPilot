using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Dal.Tests;

public sealed class VictronMqttTopicFactoryTests
{
    [Fact]
    public void CreateResolvesPortalIdPlaceholders()
    {
        var settings = new VictronMqttSettings
        {
            Host = "192.168.69.92",
            Port = 1883,
            PortalId = "c0619ab93165",
            KeepAliveSeconds = 15,
            StaleAfterSeconds = 30,
            DryRun = true,
            GridPowerTopicTemplate = "N/{portalId}/grid/30/Ac/Power",
            BatterySocTopicTemplate = "N/{portalId}/battery/512/Soc",
            BatteryPowerTopicTemplate = "N/{portalId}/battery/512/Dc/0/Power",
            HouseConsumptionTopicTemplate = "N/{portalId}/system/0/Ac/Consumption/L1/Power",
            ChargeDischargeSetpointTopic = "settings/0/Settings/CGwacs/AcPowerSetPoint"
        };

        var topics = VictronMqttTopicFactory.Create(settings);

        Assert.Equal("N/c0619ab93165/grid/30/Ac/Power", topics.GridPowerTopic);
        Assert.Equal("N/c0619ab93165/system/0/Ac/Consumption/L1/Power", topics.HouseConsumptionTopic);
    }
}
