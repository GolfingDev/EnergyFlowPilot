namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents one persisted live energy sample from the device feed.
/// </summary>
public sealed class LiveConsumptionSample
{
    public LiveConsumptionSample(decimal houseConsumptionWatts, DateTimeOffset measuredAtUtc)
        : this(
            houseConsumptionWatts,
            measuredAtUtc,
            gridPowerWatts: null,
            batteryPowerWatts: null,
            batterySocPercent: null,
            pvProductionWatts: null)
    {
    }

    public LiveConsumptionSample(
        decimal houseConsumptionWatts,
        DateTimeOffset measuredAtUtc,
        decimal? gridPowerWatts,
        decimal? batteryPowerWatts,
        decimal? batterySocPercent,
        decimal? pvProductionWatts)
    {
        if (measuredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Messzeitpunkt fuer Live-Verbrauch muss in UTC angegeben sein.", nameof(measuredAtUtc));
        }

        HouseConsumptionWatts = houseConsumptionWatts;
        MeasuredAtUtc = measuredAtUtc;
        GridPowerWatts = gridPowerWatts;
        BatteryPowerWatts = batteryPowerWatts;
        BatterySocPercent = batterySocPercent;
        PvProductionWatts = pvProductionWatts;
    }

    public decimal HouseConsumptionWatts { get; }

    public DateTimeOffset MeasuredAtUtc { get; }

    public decimal? GridPowerWatts { get; }

    /// <summary>
    /// Gets measured battery power in watts. Positive values mean battery charging.
    /// </summary>
    public decimal? BatteryPowerWatts { get; }

    public decimal? BatterySocPercent { get; }

    public decimal? PvProductionWatts { get; }
}
