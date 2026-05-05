namespace TibberVictronController.Dal.Entities;

public sealed class ConsumptionDayProfileEntity
{
    public int DayOfWeek { get; set; }

    public int SlotIndex { get; set; }

    public decimal AverageConsumptionWatts { get; set; }

    public int SampleCount { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
