using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

/// <summary>
/// Expands weekly series into single trips. Each occurrence keeps the local (Europe/Berlin) departure time of the
/// first trip, so 07:30 stays 07:30 across daylight-saving changes; an entered return keeps its duration.
/// </summary>
public static class EvTripOccurrences
{
    public static IReadOnlyList<EvTrip> Expand(IEnumerable<EvTrip> trips, DateTimeOffset nowUtc, TimeSpan horizon, TimeZoneInfo zone)
    {
        var until = nowUtc + horizon;
        var result = new List<EvTrip>();
        foreach (var trip in trips)
        {
            if (!trip.IsRecurring)
            {
                result.Add(trip);
                continue;
            }

            var firstLocal = TimeZoneInfo.ConvertTime(trip.DepartureAtUtc, zone);
            var duration = trip.ReturnAtUtc - trip.DepartureAtUtc;
            var end = trip.RecurrenceUntilUtc is { } seriesEnd && seriesEnd < until ? seriesEnd : until;
            // Start no earlier than yesterday so a trip that is still under way stays visible.
            var nowLocalDate = TimeZoneInfo.ConvertTime(nowUtc, zone).Date.AddDays(-1);
            for (var date = firstLocal.Date > nowLocalDate ? firstLocal.Date : nowLocalDate; ; date = date.AddDays(1))
            {
                var local = date + firstLocal.TimeOfDay;
                var departure = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
                if (departure > end) break;
                if ((trip.RecurrenceDays & (1 << (int)date.DayOfWeek)) == 0 || departure < trip.DepartureAtUtc) continue;
                var occurrence = DateOnly.FromDateTime(date);
                // A skipped date stays visible as an ignored occurrence so it can be brought back.
                result.Add(trip with
                {
                    DepartureAtUtc = departure,
                    ReturnAtUtc = departure + duration,
                    OccurrenceDate = occurrence,
                    IsIgnored = trip.IsIgnored || trip.SkippedDates?.Contains(occurrence) == true
                });
            }
        }

        return result.OrderBy(trip => trip.DepartureAtUtc).ToArray();
    }
}
