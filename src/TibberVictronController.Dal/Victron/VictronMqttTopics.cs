namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Contains fully resolved Victron MQTT topics for subscriptions and future writes.
/// </summary>
public sealed class VictronMqttTopics
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
