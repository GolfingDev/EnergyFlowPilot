using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Publishes the signed charge/discharge setpoint to the configured Victron MQTT write topic.
/// </summary>
public sealed class MqttVictronSetpointPublisher : IVictronSetpointPublisher
{
    private const decimal ChargeCurrentLimitSafetyFactor = 0.90m;

    private readonly DatabaseVictronMqttSettingsProvider settingsProvider;
    private readonly IVictronMqttControlClient mqttControlClient;
    private readonly VictronSetpointRefreshState refreshState;
    private readonly ILogger<MqttVictronSetpointPublisher> logger;

    public MqttVictronSetpointPublisher(
        DatabaseVictronMqttSettingsProvider settingsProvider,
        IVictronMqttControlClient mqttControlClient,
        VictronSetpointRefreshState refreshState,
        ILogger<MqttVictronSetpointPublisher> logger)
    {
        this.settingsProvider = settingsProvider;
        this.mqttControlClient = mqttControlClient;
        this.refreshState = refreshState;
        this.logger = logger;
    }

    public async Task PublishAsync(CurrentBatteryDecisionResult decisionResult, CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var topics = VictronMqttTopicFactory.Create(settings);
        var effectiveTargetPowerWatts = CalculateEffectiveTargetPowerWatts(decisionResult, mqttControlClient, topics, logger);
        var gridSetpointWatts = CalculateGridSetpointWatts(decisionResult, effectiveTargetPowerWatts);
        var hub4Control = CalculateHub4Control(decisionResult, settings.BatteryIdleThresholdWatts, effectiveTargetPowerWatts);

        await PublishHub4ModeAsync(mqttControlClient, settings, topics, logger, cancellationToken);
        await PublishHub4ControlAsync(mqttControlClient, settings, topics, logger, hub4Control, cancellationToken);
        var setpoints = CreateSetpoints(settings, topics, gridSetpointWatts, decisionResult, effectiveTargetPowerWatts);
        await PublishSetpointsAsync(mqttControlClient, setpoints, cancellationToken);
        refreshState.Update(new VictronSetpointRefreshSnapshot(decisionResult.ValidToUtc, setpoints));

        logger.LogInformation(
            "Victron-Setpoint per MQTT veröffentlicht. ControlMode={ControlMode}, GridSetpointW={GridSetpointWatts}, ExternalSetpointW={ExternalSetpointWatts}, EffectiveTargetPowerW={EffectiveTargetPowerWatts}, DisableCharge={DisableCharge}, DisableFeedIn={DisableFeedIn}",
            settings.ControlMode,
            gridSetpointWatts,
            CalculateExternalEssSetpointWatts(decisionResult, effectiveTargetPowerWatts),
            effectiveTargetPowerWatts,
            hub4Control.DisableCharge,
            hub4Control.DisableFeedIn);
    }

    public static int CalculateGridSetpointWatts(CurrentBatteryDecisionResult decisionResult)
    {
        return CalculateGridSetpointWatts(decisionResult, decisionResult.Decision.TargetPowerWatts);
    }

    private static int CalculateGridSetpointWatts(CurrentBatteryDecisionResult decisionResult, int targetPowerWatts)
    {
        var desiredBatteryPowerWatts = decisionResult.Decision.Instruction.DecisionState switch
        {
            BatteryDecisionState.Charge => targetPowerWatts,
            BatteryDecisionState.Discharge => -targetPowerWatts,
            _ => 0
        };

        if (decisionResult.SiteTelemetry.CurrentBatteryPowerWatts is null)
        {
            return decisionResult.Decision.Instruction.DecisionState switch
            {
                BatteryDecisionState.Charge => decisionResult.SiteTelemetry.CurrentGridImportWatts + targetPowerWatts,
                BatteryDecisionState.Discharge => decisionResult.SiteTelemetry.CurrentGridImportWatts - targetPowerWatts,
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
        return CalculateHub4Control(decisionResult, batteryIdleThresholdWatts, decisionResult.Decision.TargetPowerWatts);
    }

    private static Hub4Control CalculateHub4Control(
        CurrentBatteryDecisionResult decisionResult,
        int batteryIdleThresholdWatts,
        int targetPowerWatts)
    {
        var thresholdWatts = Math.Max(0, batteryIdleThresholdWatts);
        var desiredBatteryPowerWatts = decisionResult.Decision.Instruction.DecisionState switch
        {
            BatteryDecisionState.Charge => targetPowerWatts,
            BatteryDecisionState.Discharge => -targetPowerWatts,
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

    private static IReadOnlyList<VictronSetpointValue> CreateSetpoints(
        VictronMqttSettings settings,
        VictronMqttTopics topics,
        int gridSetpointWatts,
        CurrentBatteryDecisionResult decisionResult,
        int effectiveTargetPowerWatts)
    {
        if (settings.ControlMode == VictronControlMode.NormalEss)
        {
            return new[]
            {
                new VictronSetpointValue(topics.ChargeDischargeSetpointTopic, gridSetpointWatts)
            };
        }

        var externalSetpointWatts = CalculateExternalEssSetpointWatts(decisionResult, effectiveTargetPowerWatts);
        var phaseSetpoints = SplitSetpointAcrossPhases(externalSetpointWatts, topics.ExternalEssAcPowerSetpointTopics.Count);
        var setpoints = new List<VictronSetpointValue>(phaseSetpoints.Count);

        for (var index = 0; index < phaseSetpoints.Count; index++)
        {
            setpoints.Add(new VictronSetpointValue(topics.ExternalEssAcPowerSetpointTopics[index], phaseSetpoints[index]));
        }

        return setpoints;
    }

    private static async Task PublishSetpointsAsync(
        IVictronMqttControlClient mqttClient,
        IReadOnlyList<VictronSetpointValue> setpoints,
        CancellationToken cancellationToken)
    {
        foreach (var setpoint in setpoints)
        {
            await PublishValueAsync(mqttClient, setpoint.Topic, setpoint.Value, cancellationToken);
        }
    }

    public static int CalculateExternalEssSetpointWatts(CurrentBatteryDecisionResult decisionResult)
    {
        return CalculateExternalEssSetpointWatts(decisionResult, decisionResult.Decision.TargetPowerWatts);
    }

    private static int CalculateExternalEssSetpointWatts(CurrentBatteryDecisionResult decisionResult, int targetPowerWatts)
    {
        return decisionResult.Decision.Instruction.DecisionState switch
        {
            BatteryDecisionState.Charge => targetPowerWatts,
            BatteryDecisionState.Discharge => -targetPowerWatts,
            _ => 0
        };
    }

    private static int CalculateEffectiveTargetPowerWatts(
        CurrentBatteryDecisionResult decisionResult,
        IVictronMqttControlClient mqttClient,
        VictronMqttTopics topics,
        ILogger logger)
    {
        if (decisionResult.Decision.Instruction.DecisionState != BatteryDecisionState.Charge ||
            !TryCalculateVebusChargePowerLimitWatts(mqttClient, topics, out var chargePowerLimitWatts))
        {
            return decisionResult.Decision.TargetPowerWatts;
        }

        var effectiveTargetPowerWatts = Math.Min(decisionResult.Decision.TargetPowerWatts, chargePowerLimitWatts);
        if (effectiveTargetPowerWatts < decisionResult.Decision.TargetPowerWatts)
        {
            logger.LogInformation(
                "Ladeziel durch VE.Bus-Ladestrombegrenzung reduziert. TargetPowerW={TargetPowerWatts}, EffectiveTargetPowerW={EffectiveTargetPowerWatts}, LimitW={ChargePowerLimitWatts}",
                decisionResult.Decision.TargetPowerWatts,
                effectiveTargetPowerWatts,
                chargePowerLimitWatts);
        }

        return effectiveTargetPowerWatts;
    }

    private static bool TryCalculateVebusChargePowerLimitWatts(
        IVictronMqttControlClient mqttClient,
        VictronMqttTopics topics,
        out int chargePowerLimitWatts)
    {
        chargePowerLimitWatts = 0;
        if (!mqttClient.TryGetLatestValue(topics.VebusMaxChargeCurrentTopic, out var maxChargeCurrentAmps) ||
            !mqttClient.TryGetLatestValue(topics.BatteryVoltageTopic, out var batteryVoltageVolts))
        {
            return false;
        }

        chargePowerLimitWatts = CalculateVebusChargePowerLimitWatts(maxChargeCurrentAmps, batteryVoltageVolts);
        return true;
    }

    public static int CalculateVebusChargePowerLimitWatts(decimal maxChargeCurrentAmps, decimal batteryVoltageVolts)
    {
        if (maxChargeCurrentAmps <= 0m || batteryVoltageVolts <= 0m)
        {
            return 0;
        }

        return (int)Math.Floor(maxChargeCurrentAmps * batteryVoltageVolts * ChargeCurrentLimitSafetyFactor);
    }

    private static async Task PublishHub4ControlAsync(
        IVictronMqttControlClient mqttClient,
        VictronMqttSettings settings,
        VictronMqttTopics topics,
        ILogger logger,
        Hub4Control hub4Control,
        CancellationToken cancellationToken)
    {
        await PublishValueIfChangedAsync(
            mqttClient,
            topics.DisableChargeTopic,
            topics.DisableChargeReadTopic,
            hub4Control.DisableCharge ? 1 : 0,
            "DisableCharge",
            logger,
            cancellationToken);
        await PublishValueIfChangedAsync(
            mqttClient,
            topics.DisableFeedInTopic,
            topics.DisableFeedInReadTopic,
            hub4Control.DisableFeedIn ? 1 : 0,
            "DisableFeedIn",
            logger,
            cancellationToken);
    }

    private static async Task PublishHub4ModeAsync(
        IVictronMqttControlClient mqttClient,
        VictronMqttSettings settings,
        VictronMqttTopics topics,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!settings.SwitchEssModeViaMqtt)
        {
            return;
        }

        var desiredMode = CalculateHub4ModeValue(settings.ControlMode);
        var hasCurrentMode = mqttClient.TryGetLatestValue(topics.Hub4ModeReadTopic, out var currentMode);
        if (!hasCurrentMode)
        {
            logger.LogDebug(
                "Hub4Mode wird nicht geschrieben, weil noch kein Readback vorliegt. ReadTopic={ReadTopic}, DesiredMode={DesiredMode}",
                topics.Hub4ModeReadTopic,
                desiredMode);
            return;
        }

        if (!ShouldPublishHub4Mode(hasCurrentMode, currentMode, desiredMode))
        {
            logger.LogDebug(
                "Hub4Mode passt bereits. ReadTopic={ReadTopic}, CurrentMode={CurrentMode}",
                topics.Hub4ModeReadTopic,
                currentMode);
            return;
        }

        await PublishValueAsync(mqttClient, topics.Hub4ModeTopic, desiredMode, cancellationToken);
    }

    public static bool ShouldPublishHub4Mode(bool hasCurrentMode, decimal currentMode, int desiredMode)
    {
        return hasCurrentMode && currentMode != desiredMode;
    }

    private static async Task PublishValueIfChangedAsync(
        IVictronMqttControlClient mqttClient,
        string writeTopic,
        string readTopic,
        int desiredValue,
        string label,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!mqttClient.TryGetLatestValue(readTopic, out var currentValue))
        {
            logger.LogDebug(
                "{Label} wird nicht geschrieben, weil noch kein Readback vorliegt. ReadTopic={ReadTopic}, DesiredValue={DesiredValue}",
                label,
                readTopic,
                desiredValue);
            return;
        }

        if (currentValue == desiredValue)
        {
            return;
        }

        await PublishValueAsync(mqttClient, writeTopic, desiredValue, cancellationToken);
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
