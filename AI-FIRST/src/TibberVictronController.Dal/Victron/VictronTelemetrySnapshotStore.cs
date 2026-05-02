namespace TibberVictronController.Dal.Victron;

/// <summary>
/// Stores the latest Victron telemetry snapshot in memory for later provider integration.
/// </summary>
public sealed class VictronTelemetrySnapshotStore
{
    private readonly Lock syncRoot = new();
    private VictronTelemetrySnapshot snapshot = new();

    public VictronTelemetrySnapshot GetSnapshot()
    {
        lock (syncRoot)
        {
            return snapshot;
        }
    }

    public void UpdateGridPower(decimal value, DateTimeOffset measuredAtUtc)
    {
        lock (syncRoot)
        {
            snapshot = new VictronTelemetrySnapshot()
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
            snapshot = new VictronTelemetrySnapshot()
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
            snapshot = new VictronTelemetrySnapshot()
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
            snapshot = new VictronTelemetrySnapshot()
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
