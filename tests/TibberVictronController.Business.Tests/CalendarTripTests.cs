using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Tests.TestDoubles;

namespace TibberVictronController.Business.Tests;

public sealed class CalendarTripTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 7, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Kundentermin in Hamburg", "Hamburg")]
    [InlineData("Messe nach Frankfurt am Main", "Frankfurt am Main")]
    [InlineData("Kunde Müller, Leipzig", "Leipzig")]
    [InlineData("Workshop @ Köln", "Köln")]
    [InlineData("Besuch in Bad Homburg", "Bad Homburg")]
    public void PlaceIsTakenFromUnambiguousSubjectPatterns(string subject, string place)
    {
        var candidate = CalendarLocationExtractor.Extract(Event("1", subject, null));

        Assert.Equal(place, candidate!.Query);
        Assert.True(candidate.FromSubject);
    }

    [Theory]
    [InlineData("Termin nach Absprache")]
    [InlineData("Jour fixe")]
    [InlineData("Arbeiten in Ruhe")]
    public void SubjectsWithoutPlaceAreSkipped(string subject) =>
        Assert.Null(CalendarLocationExtractor.Extract(Event("1", subject, null)));

    [Fact]
    public void LocationFieldWinsAndOnlineMeetingsAreNoTrips()
    {
        Assert.Equal("Hauptstraße 1, 04109 Leipzig", CalendarLocationExtractor.Extract(Event("1", "in Hamburg", "Hauptstraße 1, 04109 Leipzig"))!.Query);
        Assert.False(CalendarLocationExtractor.Extract(Event("1", "x", "Leipzig"))!.FromSubject);
        Assert.Null(CalendarLocationExtractor.Extract(Event("2", "Abstimmung in Hamburg", "Microsoft Teams-Besprechung")));
        Assert.Null(CalendarLocationExtractor.Extract(Event("3", "Abstimmung in Hamburg", null) with { IsOnlineMeeting = true }));
    }

    [Fact]
    public async Task SyncCreatesTripsWithDrivingTimeAndReusesTheRoute()
    {
        var context = new SyncContext();
        context.Calendar.Events.Add(Event("e1", "Kunde", "Leipzig", Now.AddDays(1), Now.AddDays(1).AddHours(2)));

        var first = await context.Service.SyncAsync(CancellationToken.None);
        context.Calendar.Events[0] = context.Calendar.Events[0] with { StartUtc = Now.AddDays(1).AddHours(1) };
        await context.Service.SyncAsync(CancellationToken.None);

        var trip = Assert.Single(context.Trips.Trips);
        Assert.Equal(1, first.TripsFound);
        Assert.Equal(Now.AddDays(1).AddHours(1).AddMinutes(-120), trip.DepartureAtUtc); // 120 min driving
        Assert.Equal(Now.AddDays(1).AddHours(2).AddMinutes(120), trip.ReturnAtUtc);
        Assert.Equal(1, context.Routes.Calls); // place unchanged: no second OpenStreetMap request
    }

    [Fact]
    public async Task RemovedAppointmentRemovesItsTripButIgnoreSurvivesUpdates()
    {
        var context = new SyncContext();
        context.Calendar.Events.Add(Event("keep", "A", "Leipzig", Now.AddDays(1), Now.AddDays(1).AddHours(1)));
        context.Calendar.Events.Add(Event("gone", "B", "Dresden", Now.AddDays(2), Now.AddDays(2).AddHours(1)));
        await context.Service.SyncAsync(CancellationToken.None);
        await context.Trips.SetIgnoredAsync(context.Trips.Trips.Single(t => t.ExternalId == "keep").Id, true);

        context.Calendar.Events.RemoveAll(e => e.Id == "gone");
        await context.Service.SyncAsync(CancellationToken.None);

        var remaining = Assert.Single(context.Trips.Trips);
        Assert.Equal("keep", remaining.ExternalId);
        Assert.True(remaining.IsIgnored);
    }

    [Fact]
    public async Task UnknownPlacesAreReportedAndCloseOnesIgnored()
    {
        var context = new SyncContext();
        context.Calendar.Events.Add(Event("x", "Treffen in Nirgendwo", null, Now.AddDays(1), Now.AddDays(1).AddHours(1)));
        context.Calendar.Events.Add(Event("h", "Zuhause", "Nachbarhaus", Now.AddDays(1), Now.AddDays(1).AddHours(1)));
        context.Calendar.Events.Add(Event("c", "Abgesagt", "Leipzig", Now.AddDays(1), Now.AddDays(1).AddHours(1)) with { IsCancelled = true });

        var result = await context.Service.SyncAsync(CancellationToken.None);

        Assert.Empty(context.Trips.Trips);
        Assert.Contains(result.Unresolved, item => item.Contains("Nirgendwo"));
        Assert.True(context.Routes.PlacesOnlyQueries.Contains("Nirgendwo"));
    }

    private static CalendarEvent Event(string id, string subject, string? location, DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        new(id, subject, location, start ?? Now.AddDays(1), end ?? Now.AddDays(1).AddHours(1), false, false, false);

    private sealed class SyncContext
    {
        public FakeCalendar Calendar { get; } = new();
        public FakeRoutes Routes { get; } = new();
        public FakeTrips Trips { get; } = new();
        public CalendarTripSyncService Service { get; }

        public SyncContext()
        {
            var settings = new FakeControllerSettingStore();
            settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.PvForecastLatitudeKey, "51.0", ControllerSettingSensitivity.Normal, Now));
            settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.PvForecastLongitudeKey, "13.0", ControllerSettingSensitivity.Normal, Now));
            Service = new CalendarTripSyncService(new CalendarTripSyncDependencies(Calendar, Routes, Trips, settings, new Clock()));
        }
    }

    private sealed class Clock : IUtcClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakeCalendar : ICalendarSource
    {
        public List<CalendarEvent> Events { get; } = [];

        public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>(Events.ToArray());
    }

    private sealed class FakeRoutes : IRoutePlanner
    {
        public int Calls { get; private set; }
        public HashSet<string> PlacesOnlyQueries { get; } = [];

        public Task<RouteEstimate> EstimateAsync(GeoPoint origin, string destination, bool placesOnly = false, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (placesOnly) PlacesOnlyQueries.Add(destination);
            return destination switch
            {
                "Nirgendwo" => throw new InvalidOperationException("nicht gefunden"),
                "Nachbarhaus" => Task.FromResult(new RouteEstimate(destination, new GeoPoint(51, 13), 0.3, 1)),
                _ => Task.FromResult(new RouteEstimate(destination + ", Deutschland", new GeoPoint(51.3, 12.4), 120, 120))
            };
        }
    }

    private sealed class FakeTrips : IEvTripRepository
    {
        private int nextId = 1;
        public List<EvTrip> Trips { get; } = [];

        public Task<EvTrip> AddAsync(EvTripDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(Trips.RemoveAll(t => t.Id == id) > 0);
        public Task<IReadOnlyList<EvTrip>> GetUpcomingAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EvTrip>>(Trips.ToArray());
        public Task<EvTrip?> GetAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(Trips.FirstOrDefault(t => t.Id == id));
        public Task<IReadOnlyList<EvTrip>> GetCalendarTripsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EvTrip>>(Trips.Where(t => t.IsFromCalendar).ToArray());

        public Task UpsertCalendarTripAsync(CalendarTripUpdate update, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            var index = Trips.FindIndex(t => t.ExternalId == update.ExternalId);
            var existing = index >= 0 ? Trips[index] : null;
            if (existing is null && update.Route is null) return Task.CompletedTask;
            var route = update.Route;
            var trip = new EvTrip(existing?.Id ?? nextId++, update.DepartureAtUtc, update.ReturnAtUtc, update.Destination,
                route?.ResolvedAddress ?? existing!.ResolvedAddress, route?.DistanceKm ?? existing!.DistanceKm, route?.DurationMinutes ?? existing!.DurationMinutes,
                nowUtc, 0, null, EvTripSources.Calendar, update.ExternalId, update.Subject, existing?.IsIgnored ?? false);
            if (index >= 0) Trips[index] = trip; else Trips.Add(trip);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
        {
            Trips.RemoveAll(t => ids.Contains(t.Id));
            return Task.CompletedTask;
        }

        public Task<bool> SetIgnoredAsync(int id, bool ignored, CancellationToken cancellationToken = default)
        {
            var index = Trips.FindIndex(t => t.Id == id);
            if (index < 0) return Task.FromResult(false);
            Trips[index] = Trips[index] with { IsIgnored = ignored };
            return Task.FromResult(true);
        }
    }
}
