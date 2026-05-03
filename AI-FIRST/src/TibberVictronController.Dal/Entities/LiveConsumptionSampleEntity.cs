namespace TibberVictronController.Dal.Entities;

public sealed class LiveConsumptionSampleEntity
{
    public long Id { get; set; }

    public DateTimeOffset MeasuredAtUtc { get; set; }

    public decimal HouseConsumptionWatts { get; set; }
}
