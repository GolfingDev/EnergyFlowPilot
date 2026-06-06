namespace TibberVictronController.Dal.Entities;

public sealed class LiveConsumptionSampleEntity
{
    public long Id { get; set; }

    public DateTimeOffset MeasuredAtUtc { get; set; }

    public decimal HouseConsumptionWatts { get; set; }

    public decimal? GridPowerWatts { get; set; }

    public decimal? BatteryPowerWatts { get; set; }

    public decimal? BatterySocPercent { get; set; }

    public decimal? PvProductionWatts { get; set; }
}
