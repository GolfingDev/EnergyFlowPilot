namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents one persisted live house consumption sample from the MQTT device feed.
/// </summary>
public sealed class LiveConsumptionSample
{
    public LiveConsumptionSample(decimal houseConsumptionWatts, DateTimeOffset measuredAtUtc)
    {
        if (measuredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Messzeitpunkt fuer Live-Verbrauch muss in UTC angegeben sein.", nameof(measuredAtUtc));
        }

        HouseConsumptionWatts = houseConsumptionWatts;
        MeasuredAtUtc = measuredAtUtc;
    }

    public decimal HouseConsumptionWatts { get; }

    public DateTimeOffset MeasuredAtUtc { get; }
}
