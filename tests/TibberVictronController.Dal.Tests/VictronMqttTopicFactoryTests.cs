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
            ControlMode = VictronControlMode.ExternalEss,
            GridPowerTopicTemplate = "N/{portalId}/grid/30/Ac/Power",
            BatterySocTopicTemplate = "N/{portalId}/battery/512/Soc",
            BatteryPowerTopicTemplate = "N/{portalId}/battery/512/Dc/0/Power",
            HouseConsumptionTopicTemplate = "N/{portalId}/system/0/Ac/Consumption/L1/Power",
            ChargeDischargeSetpointTopic = "W/{portalId}/settings/0/Settings/CGwacs/AcPowerSetPoint",
            Hub4ModeTopic = "W/{portalId}/settings/0/Settings/CGwacs/Hub4Mode",
            SwitchEssModeViaMqtt = true,
            ExternalEssPhaseCount = 3,
            ExternalEssL1AcPowerSetpointTopic = "W/{portalId}/vebus/276/Hub4/L1/AcPowerSetpoint",
            ExternalEssL2AcPowerSetpointTopic = "W/{portalId}/vebus/276/Hub4/L2/AcPowerSetpoint",
            ExternalEssL3AcPowerSetpointTopic = "W/{portalId}/vebus/276/Hub4/L3/AcPowerSetpoint",
            DisableChargeTopic = "W/{portalId}/vebus/276/Hub4/DisableCharge",
            DisableFeedInTopic = "W/{portalId}/vebus/276/Hub4/DisableFeedIn",
            BatteryIdleThresholdWatts = 100
        };

        var topics = VictronMqttTopicFactory.Create(settings);

        Assert.Equal("N/c0619ab93165/grid/30/Ac/Power", topics.GridPowerTopic);
        Assert.Equal("N/c0619ab93165/system/0/Ac/Consumption/L1/Power", topics.HouseConsumptionTopic);
        Assert.Equal("W/c0619ab93165/settings/0/Settings/CGwacs/AcPowerSetPoint", topics.ChargeDischargeSetpointTopic);
        Assert.Equal("W/c0619ab93165/settings/0/Settings/CGwacs/Hub4Mode", topics.Hub4ModeTopic);
        Assert.Equal(new[]
        {
            "W/c0619ab93165/vebus/276/Hub4/L1/AcPowerSetpoint",
            "W/c0619ab93165/vebus/276/Hub4/L2/AcPowerSetpoint",
            "W/c0619ab93165/vebus/276/Hub4/L3/AcPowerSetpoint"
        }, topics.ExternalEssAcPowerSetpointTopics);
        Assert.Equal("W/c0619ab93165/vebus/276/Hub4/DisableCharge", topics.DisableChargeTopic);
        Assert.Equal("W/c0619ab93165/vebus/276/Hub4/DisableFeedIn", topics.DisableFeedInTopic);
    }
}
