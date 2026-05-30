using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Publishes the signed charge/discharge setpoint to the configured Victron MQTT write topic.
/// </summary>
public sealed class MqttVictronSetpointPublisher : IVictronSetpointPublisher
{
    private readonly DatabaseVictronMqttSettingsProvider settingsProvider;
    private readonly IVictronMqttControlClient mqttControlClient;
    private readonly ILogger<MqttVictronSetpointPublisher> logger;

    public MqttVictronSetpointPublisher(
        DatabaseVictronMqttSettingsProvider settingsProvider,
        IVictronMqttControlClient mqttControlClient,
        ILogger<MqttVictronSetpointPublisher> logger)
    {
        this.settingsProvider = settingsProvider;
        this.mqttControlClient = mqttControlClient;
        this.logger = logger;
    }

    public async Task PublishAsync(CurrentBatteryDecisionResult decisionResult, CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var topics = VictronMqttTopicFactory.Create(settings);
        var gridSetpointWatts = CalculateGridSetpointWatts(decisionResult);
        var hub4Control = CalculateHub4Control(decisionResult, settings.BatteryIdleThresholdWatts);

        await PublishHub4ModeAsync(mqttControlClient, settings, topics, cancellationToken);
        await PublishHub4ControlAsync(mqttControlClient, settings, topics, hub4Control, cancellationToken);
        await PublishSetpointAsync(mqttControlClient, settings, topics, gridSetpointWatts, decisionResult, cancellationToken);

        logger.LogInformation(
            "Victron-Setpoint per MQTT veroeffentlicht. ControlMode={ControlMode}, GridSetpointW={GridSetpointWatts}, ExternalSetpointW={ExternalSetpointWatts}, DisableCharge={DisableCharge}, DisableFeedIn={DisableFeedIn}",
            settings.ControlMode,
            gridSetpointWatts,
            CalculateExternalEssSetpointWatts(decisionResult),
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
        IVictronMqttControlClient mqttClient,
        string topic,
        int value,
        CancellationToken cancellationToken)
    {
        await mqttClient.PublishValueAsync(topic, value, cancellationToken);
    }

    private static async Task PublishSetpointAsync(
        IVictronMqttControlClient mqttClient,
        VictronMqttSettings settings,
        VictronMqttTopics topics,
        int gridSetpointWatts,
        CurrentBatteryDecisionResult decisionResult,
        CancellationToken cancellationToken)
    {
        if (settings.ControlMode == VictronControlMode.NormalEss)
        {
            await PublishValueAsync(mqttClient, topics.ChargeDischargeSetpointTopic, gridSetpointWatts, cancellationToken);
            return;
        }

        var externalSetpointWatts = CalculateExternalEssSetpointWatts(decisionResult);
        var phaseSetpoints = SplitSetpointAcrossPhases(externalSetpointWatts, topics.ExternalEssAcPowerSetpointTopics.Count);

        for (var index = 0; index < phaseSetpoints.Count; index++)
        {
            await PublishValueAsync(mqttClient, topics.ExternalEssAcPowerSetpointTopics[index], phaseSetpoints[index], cancellationToken);
        }
    }

    public static int CalculateExternalEssSetpointWatts(CurrentBatteryDecisionResult decisionResult)
    {
        return decisionResult.Decision.Instruction.DecisionState switch
        {
            BatteryDecisionState.Charge => decisionResult.Decision.TargetPowerWatts,
            BatteryDecisionState.Discharge => -decisionResult.Decision.TargetPowerWatts,
            _ => 0
        };
    }

    private static async Task PublishHub4ControlAsync(
        IVictronMqttControlClient mqttClient,
        VictronMqttSettings settings,
        VictronMqttTopics topics,
        Hub4Control hub4Control,
        CancellationToken cancellationToken)
    {
        await PublishValueAsync(mqttClient, topics.DisableChargeTopic, hub4Control.DisableCharge ? 1 : 0, cancellationToken);
        await PublishValueAsync(mqttClient, topics.DisableFeedInTopic, hub4Control.DisableFeedIn ? 1 : 0, cancellationToken);

        if (settings.ControlMode == VictronControlMode.NormalEss)
        {
            return;
        }

        foreach (var setpointTopic in topics.ExternalEssAcPowerSetpointTopics)
        {
            await PublishValueAsync(mqttClient, CreateExternalEssPhaseControlTopic(setpointTopic, "DisableCharge"), hub4Control.DisableCharge ? 1 : 0, cancellationToken);
            await PublishValueAsync(mqttClient, CreateExternalEssPhaseControlTopic(setpointTopic, "DisableFeedIn"), hub4Control.DisableFeedIn ? 1 : 0, cancellationToken);
        }
    }

    public static string CreateExternalEssPhaseControlTopic(string acPowerSetpointTopic, string controlPath)
    {
        const string setpointSuffix = "/AcPowerSetpoint";

        if (!acPowerSetpointTopic.EndsWith(setpointSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Das External-ESS-Setpoint-Topic muss mit /AcPowerSetpoint enden.", nameof(acPowerSetpointTopic));
        }

        return acPowerSetpointTopic[..^setpointSuffix.Length] + "/" + controlPath;
    }

    private static async Task PublishHub4ModeAsync(
        IVictronMqttControlClient mqttClient,
        VictronMqttSettings settings,
        VictronMqttTopics topics,
        CancellationToken cancellationToken)
    {
        if (!settings.SwitchEssModeViaMqtt)
        {
            return;
        }

        await PublishValueAsync(mqttClient, topics.Hub4ModeTopic, CalculateHub4ModeValue(settings.ControlMode), cancellationToken);
    }

    public static int CalculateHub4ModeValue(VictronControlMode controlMode)
    {
        return controlMode switch
        {
            VictronControlMode.ExternalEss => 3,
            _ => 1
        };
    }

    public static IReadOnlyList<int> SplitSetpointAcrossPhases(int totalSetpointWatts, int phaseCount)
    {
        if (phaseCount is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(phaseCount), "Die Phasenanzahl muss zwischen 1 und 3 liegen.");
        }

        var baseSetpointWatts = totalSetpointWatts / phaseCount;
        var remainderWatts = totalSetpointWatts % phaseCount;
        var setpoints = new int[phaseCount];

        for (var index = 0; index < phaseCount; index++)
        {
            var remainderAdjustment = Math.Sign(remainderWatts) * (Math.Abs(remainderWatts) > index ? 1 : 0);
            setpoints[index] = baseSetpointWatts + remainderAdjustment;
        }

        return setpoints;
    }
}

public sealed record Hub4Control(bool DisableCharge, bool DisableFeedIn);
