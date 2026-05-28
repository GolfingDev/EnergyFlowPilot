using System.Text.Json;
using MQTTnet;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Publishes the signed charge/discharge setpoint to the configured Victron MQTT write topic.
/// </summary>
public sealed class MqttVictronSetpointPublisher : IVictronSetpointPublisher
{
    private readonly DatabaseVictronMqttSettingsProvider settingsProvider;
    private readonly ILogger<MqttVictronSetpointPublisher> logger;

    public MqttVictronSetpointPublisher(
        DatabaseVictronMqttSettingsProvider settingsProvider,
        ILogger<MqttVictronSetpointPublisher> logger)
    {
        this.settingsProvider = settingsProvider;
        this.logger = logger;
    }

    public async Task PublishAsync(CurrentBatteryDecisionResult decisionResult, CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var topics = VictronMqttTopicFactory.Create(settings);
        var gridSetpointWatts = ToGridSetpointWatts(decisionResult);
        var payload = JsonSerializer.Serialize(new { value = gridSetpointWatts });

        using var mqttClient = new MqttClientFactory().CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(settings.KeepAliveSeconds))
            .WithClientId($"tibber-victron-controller-setpoint-{Guid.NewGuid():N}")
            .Build();
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topics.ChargeDischargeSetpointTopic)
            .WithPayload(payload)
            .Build();

        await mqttClient.ConnectAsync(options, cancellationToken);
        await mqttClient.PublishAsync(message, cancellationToken);
        await mqttClient.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken);

        logger.LogInformation(
            "Victron-Setpoint per MQTT veroeffentlicht. Topic={Topic}, SetpointW={SetpointWatts}",
            topics.ChargeDischargeSetpointTopic,
            gridSetpointWatts);
    }

    private static int ToGridSetpointWatts(CurrentBatteryDecisionResult decisionResult)
    {
        return decisionResult.Decision.Instruction.DecisionState switch
        {
            BatteryDecisionState.Charge => decisionResult.SiteTelemetry.CurrentGridImportWatts + decisionResult.Decision.TargetPowerWatts,
            BatteryDecisionState.Discharge => decisionResult.SiteTelemetry.CurrentGridImportWatts - decisionResult.Decision.TargetPowerWatts,
            _ => decisionResult.SiteTelemetry.CurrentGridImportWatts
        };
    }
}
