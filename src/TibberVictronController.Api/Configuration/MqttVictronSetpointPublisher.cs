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
        var gridSetpointWatts = CalculateGridSetpointWatts(decisionResult);
        var hub4Control = CalculateHub4Control(decisionResult, settings.BatteryIdleThresholdWatts);

        using var mqttClient = new MqttClientFactory().CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(settings.KeepAliveSeconds))
            .WithClientId($"tibber-victron-controller-setpoint-{Guid.NewGuid():N}")
            .Build();

        await mqttClient.ConnectAsync(options, cancellationToken);
        await PublishValueAsync(mqttClient, topics.DisableChargeTopic, hub4Control.DisableCharge ? 1 : 0, cancellationToken);
        await PublishValueAsync(mqttClient, topics.DisableFeedInTopic, hub4Control.DisableFeedIn ? 1 : 0, cancellationToken);
        await PublishValueAsync(mqttClient, topics.ChargeDischargeSetpointTopic, gridSetpointWatts, cancellationToken);
        await mqttClient.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken);

        logger.LogInformation(
            "Victron-Setpoint per MQTT veroeffentlicht. Topic={Topic}, SetpointW={SetpointWatts}, DisableCharge={DisableCharge}, DisableFeedIn={DisableFeedIn}",
            topics.ChargeDischargeSetpointTopic,
            gridSetpointWatts,
            hub4Control.DisableCharge,
            hub4Control.DisableFeedIn);
    }

    public static int CalculateGridSetpointWatts(CurrentBatteryDecisionResult decisionResult)
    {
        var desiredBatteryPowerWatts = decisionResult.Decision.Instruction.DecisionState switch
        {
            BatteryDecisionState.Charge => decisionResult.Decision.TargetPowerWatts,
            BatteryDecisionState.Discharge => -decisionResult.Decision.TargetPowerWatts,
            _ => 0
        };

        if (decisionResult.SiteTelemetry.CurrentBatteryPowerWatts is null)
        {
            return decisionResult.Decision.Instruction.DecisionState switch
            {
                BatteryDecisionState.Charge => decisionResult.SiteTelemetry.CurrentGridImportWatts + decisionResult.Decision.TargetPowerWatts,
                BatteryDecisionState.Discharge => decisionResult.SiteTelemetry.CurrentGridImportWatts - decisionResult.Decision.TargetPowerWatts,
                _ => decisionResult.SiteTelemetry.CurrentGridImportWatts
            };
        }

        var gridPowerWithoutBatteryActionWatts =
            decisionResult.SiteTelemetry.CurrentGridImportWatts -
            decisionResult.SiteTelemetry.CurrentBatteryPowerWatts.Value;

        return gridPowerWithoutBatteryActionWatts + desiredBatteryPowerWatts;
    }

    public static Hub4Control CalculateHub4Control(
        CurrentBatteryDecisionResult decisionResult,
        int batteryIdleThresholdWatts)
    {
        var thresholdWatts = Math.Max(0, batteryIdleThresholdWatts);
        var desiredBatteryPowerWatts = decisionResult.Decision.Instruction.DecisionState switch
        {
            BatteryDecisionState.Charge => decisionResult.Decision.TargetPowerWatts,
            BatteryDecisionState.Discharge => -decisionResult.Decision.TargetPowerWatts,
            _ => 0
        };

        if (Math.Abs(desiredBatteryPowerWatts) <= thresholdWatts)
        {
            return new Hub4Control(DisableCharge: true, DisableFeedIn: true);
        }

        return desiredBatteryPowerWatts > 0
            ? new Hub4Control(DisableCharge: false, DisableFeedIn: true)
            : new Hub4Control(DisableCharge: true, DisableFeedIn: false);
    }

    private static async Task PublishValueAsync(
        IMqttClient mqttClient,
        string topic,
        int value,
        CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(new { value }))
            .Build();

        await mqttClient.PublishAsync(message, cancellationToken);
    }
}

public sealed record Hub4Control(bool DisableCharge, bool DisableFeedIn);
