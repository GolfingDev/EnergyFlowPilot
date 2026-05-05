namespace TibberVictronController.Dal.Mqtt;

/// <summary>
/// Contains persisted MQTT connection and topic settings for a compatible device integration.
/// </summary>
public sealed class MqttDeviceSettings
{
    public required string Host { get; init; }

    public int Port { get; init; }

    public required string DeviceId { get; init; }

    public int KeepAliveSeconds { get; init; }

    public int StaleAfterSeconds { get; init; }

    public bool DryRun { get; init; }

    public required string GridPowerTopicTemplate { get; init; }

    public required string BatterySocTopicTemplate { get; init; }

    public required string BatteryPowerTopicTemplate { get; init; }

    public required string HouseConsumptionTopicTemplate { get; init; }

    public required string ChargeDischargeSetpointTopic { get; init; }
}
