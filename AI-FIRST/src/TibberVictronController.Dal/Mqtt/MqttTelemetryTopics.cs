namespace TibberVictronController.Dal.Mqtt;

/// <summary>
/// Contains fully resolved MQTT topics for subscriptions and future writes.
/// </summary>
public sealed class MqttTelemetryTopics
{
    public required string GridPowerTopic { get; init; }

    public required string BatterySocTopic { get; init; }

    public required string BatteryPowerTopic { get; init; }

    public required string HouseConsumptionTopic { get; init; }

    public required string ChargeDischargeSetpointTopic { get; init; }

    public IReadOnlyList<string> ReadTopics => new[]
    {
        GridPowerTopic,
        BatterySocTopic,
        BatteryPowerTopic,
        HouseConsumptionTopic
    };
}
