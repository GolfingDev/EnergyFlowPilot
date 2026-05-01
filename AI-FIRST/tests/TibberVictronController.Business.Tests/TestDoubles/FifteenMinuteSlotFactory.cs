using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests.TestDoubles;

internal static class FifteenMinuteSlotFactory
{
    private static readonly TimeSpan ForecastSlotDuration = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<ForecastTimeSlot> CreateSlots(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        ValidateUtcRange(startsAtUtc, endsAtUtc);

        var slots = new List<ForecastTimeSlot>();

        // The test doubles intentionally share one slot factory so all Decision Engine inputs line up on the same 15-minute grid.
        for (var slotStartUtc = startsAtUtc; slotStartUtc < endsAtUtc; slotStartUtc = slotStartUtc.Add(ForecastSlotDuration))
        {
            var slotEndUtc = slotStartUtc.Add(ForecastSlotDuration);

            if (slotEndUtc > endsAtUtc)
            {
                throw new ArgumentException("Der angeforderte Zeitraum muss in 15-Minuten-Forecast-Zeitabschnitte aufteilbar sein.", nameof(endsAtUtc));
            }

            slots.Add(new ForecastTimeSlot(slotStartUtc, slotEndUtc));
        }

        return slots;
    }

    private static void ValidateUtcRange(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Start des Forecast-Zeitraums muss in UTC angegeben sein.", nameof(startsAtUtc));
        }

        if (endsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Das Ende des Forecast-Zeitraums muss in UTC angegeben sein.", nameof(endsAtUtc));
        }

        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("Das Ende des Forecast-Zeitraums muss nach dem Start liegen.", nameof(endsAtUtc));
        }
    }
}
