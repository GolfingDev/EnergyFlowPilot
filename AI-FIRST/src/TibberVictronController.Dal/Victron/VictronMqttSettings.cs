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

    public required string GridPowerTopicTemplate { get; init; }

    public required string BatterySocTopicTemplate { get; init; }

    public required string BatteryPowerTopicTemplate { get; init; }

    public required string HouseConsumptionTopicTemplate { get; init; }

    public required string ChargeDischargeSetpointTopic { get; init; }
}
