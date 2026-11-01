using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class EvTripOccurrencesTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly int TuesdayThursday = EvTrip.MaskOf([DayOfWeek.Tuesday, DayOfWeek.Thursday]);

    [Fact]
    public void SeriesRepeatsOnTheSelectedWeekdaysWithinTheHorizon()
    {
        var now = new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.Zero); // Monday
        var first = new DateTimeOffset(2026, 11, 3, 6, 30, 0, TimeSpan.Zero); // Tuesday 07:30 Berlin
        var series = Trip(first, first.AddHours(10), TuesdayThursday);

        var trips = EvTripOccurrences.Expand([series], now, TimeSpan.FromDays(14), Berlin);

        Assert.Equal(4, trips.Count); // Tue 3, Thu 5, Tue 10, Thu 12
        Assert.All(trips, trip => Assert.Contains(TimeZoneInfo.ConvertTime(trip.DepartureAtUtc, Berlin).DayOfWeek, new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday }));
        Assert.All(trips, trip => Assert.Equal(TimeSpan.FromHours(10), trip.ReturnAtUtc - trip.DepartureAtUtc));
        Assert.All(trips, trip => Assert.Equal(series.Id, trip.Id));
    }

    [Fact]
    public void SeriesEndsAtItsEndDate()
    {
        var now = new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);
        var first = new DateTimeOffset(2026, 11, 3, 6, 30, 0, TimeSpan.Zero);
        var series = Trip(first, null, TuesdayThursday, until: new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero));

        var trips = EvTripOccurrences.Expand([series], now, TimeSpan.FromDays(14), Berlin);

        Assert.Equal(2, trips.Count);
        Assert.All(trips, trip => Assert.Null(trip.ReturnAtUtc));
    }

    [Fact]
    public void LocalDepartureTimeSurvivesTheDaylightSavingChange()
    {
        // Summer time ends on 25 October 2026: 07:30 Berlin is 05:30 UTC before and 06:30 UTC after.
        var now = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var first = new DateTimeOffset(2026, 10, 20, 5, 30, 0, TimeSpan.Zero); // Tuesday 07:30 CEST
        var series = Trip(first, null, EvTrip.MaskOf([DayOfWeek.Tuesday]));

        var trips = EvTripOccurrences.Expand([series], now, TimeSpan.FromDays(10), Berlin);

        Assert.Equal(new DateTimeOffset(2026, 10, 27, 6, 30, 0, TimeSpan.Zero), trips[1].DepartureAtUtc);
        Assert.All(trips, trip => Assert.Equal(new TimeSpan(7, 30, 0), TimeZoneInfo.ConvertTime(trip.DepartureAtUtc, Berlin).TimeOfDay));
    }

    [Fact]
    public void SingleTripsPassThroughUnchanged()
    {
        var now = new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);
        var single = Trip(now.AddDays(3), null, 0);

        Assert.Equal(single, Assert.Single(EvTripOccurrences.Expand([single], now, TimeSpan.FromDays(14), Berlin)));
    }

    private static EvTrip Trip(DateTimeOffset departure, DateTimeOffset? returnAt, int days, DateTimeOffset? until = null) =>
        new(7, departure, returnAt, "Büro", "Büro, Musterstadt", 25, 30, departure, days, until);
}
