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
            ChargeDischargeSetpointTopic = Resolve(settings.ChargeDischargeSetpointTopic, settings.PortalId),
            Hub4ModeTopic = Resolve(settings.Hub4ModeTopic, settings.PortalId),
            ExternalEssAcPowerSetpointTopics = ResolveExternalEssAcPowerSetpointTopics(settings),
            DisableChargeTopic = Resolve(settings.DisableChargeTopic, settings.PortalId),
            DisableFeedInTopic = Resolve(settings.DisableFeedInTopic, settings.PortalId)
        };
    }

    private static IReadOnlyList<string> ResolveExternalEssAcPowerSetpointTopics(VictronMqttSettings settings)
    {
        var topicTemplates = new[]
        {
            settings.ExternalEssL1AcPowerSetpointTopic,
            settings.ExternalEssL2AcPowerSetpointTopic,
            settings.ExternalEssL3AcPowerSetpointTopic
        };

        return topicTemplates
            .Take(settings.ExternalEssPhaseCount)
            .Select(topicTemplate => Resolve(topicTemplate, settings.PortalId))
            .ToArray();
    }

    private static string Resolve(string topicTemplate, string portalId)
    {
        return topicTemplate.Replace("{portalId}", portalId, StringComparison.OrdinalIgnoreCase);
    }
}
