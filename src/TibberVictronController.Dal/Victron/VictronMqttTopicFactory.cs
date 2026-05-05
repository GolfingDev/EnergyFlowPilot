namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Resolves persisted Victron topic templates to concrete MQTT topics.
/// </summary>
public static class VictronMqttTopicFactory
{
    public static VictronMqttTopics Create(VictronMqttSettings settings)
    {
        return new VictronMqttTopics
        {
            GridPowerTopic = Resolve(settings.GridPowerTopicTemplate, settings.PortalId),
            BatterySocTopic = Resolve(settings.BatterySocTopicTemplate, settings.PortalId),
            BatteryPowerTopic = Resolve(settings.BatteryPowerTopicTemplate, settings.PortalId),
            HouseConsumptionTopic = Resolve(settings.HouseConsumptionTopicTemplate, settings.PortalId),
            ChargeDischargeSetpointTopic = settings.ChargeDischargeSetpointTopic
        };
    }

    private static string Resolve(string topicTemplate, string portalId)
    {
        return topicTemplate.Replace("{portalId}", portalId, StringComparison.OrdinalIgnoreCase);
    }
}
