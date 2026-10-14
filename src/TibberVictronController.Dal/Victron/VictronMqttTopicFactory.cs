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
            HouseConsumptionTopics = ResolveHouseConsumptionTopics(settings),
            ChargeDischargeSetpointTopic = Resolve(settings.ChargeDischargeSetpointTopic, settings.PortalId),
            Hub4ModeTopic = Resolve(settings.Hub4ModeTopic, settings.PortalId),
            Hub4ModeReadTopic = ToReadTopic(Resolve(settings.Hub4ModeTopic, settings.PortalId)),
            VebusMaxChargeCurrentTopic = $"N/{settings.PortalId}/vebus/276/Dc/0/MaxChargeCurrent",
            BatteryVoltageTopic = $"N/{settings.PortalId}/battery/512/Dc/0/Voltage",
            ExternalEssAcPowerSetpointTopics = ResolveExternalEssAcPowerSetpointTopics(settings),
            DisableChargeTopic = Resolve(settings.DisableChargeTopic, settings.PortalId),
            DisableChargeReadTopic = ToReadTopic(Resolve(settings.DisableChargeTopic, settings.PortalId)),
            DisableFeedInTopic = Resolve(settings.DisableFeedInTopic, settings.PortalId),
            DisableFeedInReadTopic = ToReadTopic(Resolve(settings.DisableFeedInTopic, settings.PortalId))
        };
    }

    private static IReadOnlyList<string> ResolveHouseConsumptionTopics(VictronMqttSettings settings)
    {
        // A comma-separated list allows summing all phases; a single-phase topic misses loads like a three-phase heat pump.
        var topics = settings.HouseConsumptionTopicTemplate
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(topicTemplate => Resolve(topicTemplate, settings.PortalId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (topics.Length == 0)
        {
            throw new InvalidOperationException("Das Victron-Topic fuer Hausverbrauch ist nicht konfiguriert.");
        }

        return topics;
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

    private static string ToReadTopic(string topic)
    {
        return topic.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
            ? "N/" + topic[2..]
            : topic;
    }
}
