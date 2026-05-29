namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Contains the persisted Victron MQTT connection and topic settings.
/// </summary>
public sealed class VictronMqttSettings
{
    public required string Host { get; init; }

    public int Port { get; init; }

    public required string PortalId { get; init; }

    public int KeepAliveSeconds { get; init; }

    public int StaleAfterSeconds { get; init; }

    public bool DryRun { get; init; }

    public VictronControlMode ControlMode { get; init; }

    public required string GridPowerTopicTemplate { get; init; }

    public required string BatterySocTopicTemplate { get; init; }

    public required string BatteryPowerTopicTemplate { get; init; }

    public required string HouseConsumptionTopicTemplate { get; init; }

    public required string ChargeDischargeSetpointTopic { get; init; }

    public required string Hub4ModeTopic { get; init; }

    public bool SwitchEssModeViaMqtt { get; init; }

    public int ExternalEssPhaseCount { get; init; }

    public required string ExternalEssL1AcPowerSetpointTopic { get; init; }

    public required string ExternalEssL2AcPowerSetpointTopic { get; init; }

    public required string ExternalEssL3AcPowerSetpointTopic { get; init; }

    public required string DisableChargeTopic { get; init; }

    public required string DisableFeedInTopic { get; init; }

    public int BatteryIdleThresholdWatts { get; init; }
}
