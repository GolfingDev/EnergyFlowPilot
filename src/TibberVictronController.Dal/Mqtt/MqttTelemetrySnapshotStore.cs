namespace TibberVictronController.Dal.Mqtt;

/// <summary>
/// Stores the latest MQTT telemetry snapshot in memory for later provider integration.
/// </summary>
public sealed class MqttTelemetrySnapshotStore
{
    private readonly Lock syncRoot = new();
    private MqttTelemetrySnapshot snapshot = new();

    public MqttTelemetrySnapshot GetSnapshot()
    {
        lock (syncRoot)
        {
            return snapshot;
        }
    }

    public void Clear(bool preserveLatestValues = false)
    {
        lock (syncRoot)
        {
            if (!preserveLatestValues)
            {
                snapshot = new MqttTelemetrySnapshot();
            }
        }
    }

    public void UpdateGridPower(decimal value, DateTimeOffset measuredAtUtc)
    {
        lock (syncRoot)
        {
            snapshot = new MqttTelemetrySnapshot()
            {
                GridPowerWatts = value,
                GridPowerMeasuredAtUtc = measuredAtUtc,
                BatterySocPercent = snapshot.BatterySocPercent,
                BatterySocMeasuredAtUtc = snapshot.BatterySocMeasuredAtUtc,
                BatteryPowerWatts = snapshot.BatteryPowerWatts,
                BatteryPowerMeasuredAtUtc = snapshot.BatteryPowerMeasuredAtUtc,
                HouseConsumptionWatts = snapshot.HouseConsumptionWatts,
                HouseConsumptionMeasuredAtUtc = snapshot.HouseConsumptionMeasuredAtUtc
            };
        }
    }

    public void UpdateBatterySoc(decimal value, DateTimeOffset measuredAtUtc)
    {
        lock (syncRoot)
        {
            snapshot = new MqttTelemetrySnapshot()
            {
                GridPowerWatts = snapshot.GridPowerWatts,
                GridPowerMeasuredAtUtc = snapshot.GridPowerMeasuredAtUtc,
                BatterySocPercent = value,
                BatterySocMeasuredAtUtc = measuredAtUtc,
                BatteryPowerWatts = snapshot.BatteryPowerWatts,
                BatteryPowerMeasuredAtUtc = snapshot.BatteryPowerMeasuredAtUtc,
                HouseConsumptionWatts = snapshot.HouseConsumptionWatts,
                HouseConsumptionMeasuredAtUtc = snapshot.HouseConsumptionMeasuredAtUtc
            };
        }
    }

    public void UpdateBatteryPower(decimal value, DateTimeOffset measuredAtUtc)
    {
        lock (syncRoot)
        {
            snapshot = new MqttTelemetrySnapshot()
            {
                GridPowerWatts = snapshot.GridPowerWatts,
                GridPowerMeasuredAtUtc = snapshot.GridPowerMeasuredAtUtc,
                BatterySocPercent = snapshot.BatterySocPercent,
                BatterySocMeasuredAtUtc = snapshot.BatterySocMeasuredAtUtc,
                BatteryPowerWatts = value,
                BatteryPowerMeasuredAtUtc = measuredAtUtc,
                HouseConsumptionWatts = snapshot.HouseConsumptionWatts,
                HouseConsumptionMeasuredAtUtc = snapshot.HouseConsumptionMeasuredAtUtc
            };
        }
    }

    public void UpdateHouseConsumption(decimal value, DateTimeOffset measuredAtUtc)
    {
        lock (syncRoot)
        {
            snapshot = new MqttTelemetrySnapshot()
            {
                GridPowerWatts = snapshot.GridPowerWatts,
                GridPowerMeasuredAtUtc = snapshot.GridPowerMeasuredAtUtc,
                BatterySocPercent = snapshot.BatterySocPercent,
                BatterySocMeasuredAtUtc = snapshot.BatterySocMeasuredAtUtc,
                BatteryPowerWatts = snapshot.BatteryPowerWatts,
                BatteryPowerMeasuredAtUtc = snapshot.BatteryPowerMeasuredAtUtc,
                HouseConsumptionWatts = value,
                HouseConsumptionMeasuredAtUtc = measuredAtUtc
            };
        }
    }
}
