namespace TibberVictronController.Dal.Mqtt;

/// <summary>
/// Holds the latest telemetry values observed from MQTT.
/// </summary>
public sealed class MqttTelemetrySnapshot
{
    public decimal? GridPowerWatts { get; init; }

    public DateTimeOffset? GridPowerMeasuredAtUtc { get; init; }

    public decimal? BatterySocPercent { get; init; }

    public DateTimeOffset? BatterySocMeasuredAtUtc { get; init; }

    public decimal? BatteryPowerWatts { get; init; }

    public DateTimeOffset? BatteryPowerMeasuredAtUtc { get; init; }

    public decimal? HouseConsumptionWatts { get; init; }

    public DateTimeOffset? HouseConsumptionMeasuredAtUtc { get; init; }
}
