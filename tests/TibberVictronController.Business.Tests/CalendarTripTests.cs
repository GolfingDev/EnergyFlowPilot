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
    public void SubjectsWithoutPlaceGiveNoFixedPlace(string subject)
    {
        // Capitalised words may still be checked as town names later; none of them is taken as a place directly.
        var candidate = CalendarLocationExtractor.Extract(Event("1", subject, null));
        Assert.True(candidate is null || candidate.Query.Length == 0);
    }

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
        Assert.Contains(result.Unresolved, item => item.Query == "Nirgendwo" && item.EventId == "x");
        Assert.True(context.Routes.PlacesOnlyQueries.Contains("Nirgendwo"));
    }

    [Fact]
    public async Task ChosenPlaceSurvivesLaterSyncsWhileTimesFollowTheCalendar()
    {
        var context = new SyncContext();
        context.Calendar.Events.Add(Event("t", "Tennis Neuhof", "Lidl", Now.AddDays(1), Now.AddDays(1).AddHours(2)));
        await context.Service.SyncAsync(CancellationToken.None);
        var trip = context.Trips.Trips.Single();
        await context.Trips.SetLocationAsync(trip.Id, "Lidl, Neuhof", new RouteEstimate("Lidl, Neuhof", new GeoPoint(50.4, 9.6), 15, 20));

        context.Calendar.Events[0] = context.Calendar.Events[0] with { StartUtc = Now.AddDays(1).AddHours(1) };
        await context.Service.SyncAsync(CancellationToken.None);

        var updated = context.Trips.Trips.Single();
        Assert.Equal("Lidl, Neuhof", updated.Destination);
        Assert.Equal(15, updated.DistanceKm);
        Assert.Equal(Now.AddDays(1).AddHours(1).AddMinutes(-20), updated.DepartureAtUtc);
    }

    [Fact]
    public async Task UnresolvedAppointmentBecomesATripAtTheChosenPlace()
    {
        var context = new SyncContext();
        context.Calendar.Events.Add(Event("x", "Treffen in Nirgendwo", null, Now.AddDays(1), Now.AddDays(1).AddHours(1)));
        var open = Assert.Single((await context.Service.SyncAsync(CancellationToken.None)).Unresolved);

        await context.Service.SetPlaceAsync(open, "Gasthaus Linde, Fulda", new GeoPoint(50.5, 9.6), CancellationToken.None);
        var result = await context.Service.SyncAsync(CancellationToken.None);

        var trip = Assert.Single(context.Trips.Trips);
        Assert.Equal("Gasthaus Linde, Fulda", trip.Destination);
        Assert.True(trip.HasLocationOverride);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public async Task TownInTheSubjectRefinesAGeneralLocation()
    {
        var context = new SyncContext();
        context.Calendar.Events.Add(Event("t", "Tennis Neuhof", "Lidl", Now.AddDays(1), Now.AddDays(1).AddHours(2)));
        context.Calendar.Events.Add(Event("n", "Tennis Neuhof", null, Now.AddDays(2), Now.AddDays(2).AddHours(2)));
        context.Calendar.Events.Add(Event("s", "Kunde X", "Hauptstraße 5, Fulda", Now.AddDays(3), Now.AddDays(3).AddHours(1)));

        await context.Service.SyncAsync(CancellationToken.None);

        Assert.Equal("Lidl, Neuhof", context.Trips.Trips.Single(t => t.ExternalId == "t").Destination);
        Assert.Equal("Neuhof", context.Trips.Trips.Single(t => t.ExternalId == "n").Destination);
        // A specific address is taken as it is.
        Assert.Equal("Hauptstraße 5, Fulda", context.Trips.Trips.Single(t => t.ExternalId == "s").Destination);
    }

    [Fact]
    public async Task EachUsersCalendarGivesTripsOwnedByThatUser()
    {
        var context = new SyncContext(legacyMailbox: null);
        context.Users.Calendars.Add(new UserCalendar("u-1", "Torsten", "torsten@example.com", Now));
        context.Users.Calendars.Add(new UserCalendar("u-2", "Carla", "carla@example.com", Now));
        context.Calendar.ByMailbox["torsten@example.com"] = [Event("a", "Kunde", "Leipzig")];
        context.Calendar.ByMailbox["carla@example.com"] = [Event("a", "Training", "Dresden")];

        await context.Service.SyncAsync(CancellationToken.None);

        // The same appointment id in two calendars stays two trips, each with its owner.
        Assert.Equal(2, context.Trips.Trips.Count);
        Assert.Equal("Torsten", context.Trips.Trips.Single(t => t.ExternalId == "u-1|a").OwnerName);
        Assert.Equal("u-2", context.Trips.Trips.Single(t => t.ExternalId == "u-2|a").OwnerId);
    }

    [Fact]
    public async Task AnUnreadableCalendarKeepsItsTripsAndADisconnectedOneLosesThem()
    {
        var context = new SyncContext(legacyMailbox: null);
        context.Users.Calendars.Add(new UserCalendar("u-1", "Torsten", "torsten@example.com", Now));
        context.Users.Calendars.Add(new UserCalendar("u-2", "Carla", "carla@example.com", Now));
        context.Calendar.ByMailbox["torsten@example.com"] = [Event("a", "Kunde", "Leipzig")];
        context.Calendar.ByMailbox["carla@example.com"] = [Event("b", "Training", "Dresden")];
        await context.Service.SyncAsync(CancellationToken.None);

        context.Calendar.Failing.Add("torsten@example.com");
        await context.Users.DeleteAsync("u-2");
        // No calendar readable this time: the sync reports it, but still drops the disconnected calendar's trip.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.SyncAsync(CancellationToken.None));

        var remaining = Assert.Single(context.Trips.Trips);
        Assert.Equal("u-1|a", remaining.ExternalId);
        Assert.Contains("Torsten", exception.Message);
    }

    [Fact]
    public void OnlyTheOwnerOrAnAdminMayChangeATrip()
    {
        var own = new EvTrip(1, Now, null, "Ziel", "Adresse", 10, 10, Now, OwnerId: "u-1", OwnerName: "Torsten");
        var legacy = own with { OwnerId = null, OwnerName = null };

        Assert.True(new EvTripActor("u-1", "Torsten", false).MayChange(own));
        Assert.False(new EvTripActor("u-2", "Carla", false).MayChange(own));
        Assert.True(new EvTripActor("u-2", "Carla", true).MayChange(own));
        Assert.False(new EvTripActor("u-2", "Carla", false).MayChange(legacy));
    }

    private static CalendarEvent Event(string id, string subject, string? location, DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        new(id, subject, location, start ?? Now.AddDays(1), end ?? Now.AddDays(1).AddHours(1), false, false, false);

    private sealed class SyncContext
    {
        public FakeCalendar Calendar { get; } = new();
        public FakeRoutes Routes { get; } = new();
        public FakeTrips Trips { get; } = new();
        public FakeUserCalendars Users { get; } = new();
        public CalendarTripSyncService Service { get; }

        // legacyMailbox: the older shared mailbox setting; null tests only per-user calendars.
        public SyncContext(string? legacyMailbox = "family@example.com")
        {
            var settings = new FakeControllerSettingStore();
            settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.PvForecastLatitudeKey, "51.0", ControllerSettingSensitivity.Normal, Now));
            settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.PvForecastLongitudeKey, "13.0", ControllerSettingSensitivity.Normal, Now));
            if (legacyMailbox is not null)
                settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.EvCalendarMailboxKey, legacyMailbox, ControllerSettingSensitivity.Normal, Now));
            Service = new CalendarTripSyncService(new CalendarTripSyncDependencies(Calendar, Routes, Trips, settings, new Clock(), Users));
        }
    }

    private sealed class Clock : IUtcClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakeCalendar : ICalendarSource
    {
        // Appointments of the shared mailbox; per-user mailboxes can get their own lists.
        public List<CalendarEvent> Events { get; } = [];
        public Dictionary<string, List<CalendarEvent>> ByMailbox { get; } = [];
        public HashSet<string> Failing { get; } = [];

        public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(string mailbox, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
        {
            if (Failing.Contains(mailbox)) throw new InvalidOperationException("Kalender nicht lesbar.");
            return Task.FromResult<IReadOnlyList<CalendarEvent>>((ByMailbox.TryGetValue(mailbox, out var own) ? own : Events).ToArray());
        }
    }

    private sealed class FakeUserCalendars : IUserCalendarRepository
    {
        public List<UserCalendar> Calendars { get; } = [];
        public Task<IReadOnlyList<UserCalendar>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UserCalendar>>(Calendars.ToArray());
        public Task<UserCalendar?> GetAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(Calendars.FirstOrDefault(c => c.UserId == userId));
        public Task SaveAsync(UserCalendar calendar, CancellationToken cancellationToken = default) { Calendars.RemoveAll(c => c.UserId == calendar.UserId); Calendars.Add(calendar); return Task.CompletedTask; }
        public Task DeleteAsync(string userId, CancellationToken cancellationToken = default) { Calendars.RemoveAll(c => c.UserId == userId); return Task.CompletedTask; }
    }

    private sealed class FakeRoutes : IRoutePlanner
    {
        public int Calls { get; private set; }
        public HashSet<string> PlacesOnlyQueries { get; } = [];

        public Task<GeoPoint?> FindSettlementNearAsync(string name, GeoPoint near, double maximumKm, CancellationToken cancellationToken = default) =>
            Task.FromResult<GeoPoint?>(name == "Neuhof" ? new GeoPoint(50.45, 9.61) : null);

        public Task<RouteEstimate> RouteToAsync(GeoPoint origin, GeoPoint destination, string label, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RouteEstimate(label, destination, 120, 120));

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
            var keepPlace = existing is { HasLocationOverride: true } && !update.IsLocationOverride;
            var trip = new EvTrip(existing?.Id ?? nextId++, update.DepartureAtUtc, update.ReturnAtUtc, keepPlace ? existing!.Destination : update.Destination,
                route?.ResolvedAddress ?? existing!.ResolvedAddress, route?.DistanceKm ?? existing!.DistanceKm, route?.DurationMinutes ?? existing!.DurationMinutes,
                nowUtc, 0, null, EvTripSources.Calendar, update.ExternalId, update.Subject, existing?.IsIgnored ?? false,
                RouteVersion: update.Route is null ? existing!.RouteVersion : update.RouteVersion,
                HasLocationOverride: update.IsLocationOverride || existing?.HasLocationOverride == true,
                OwnerId: update.OwnerId, OwnerName: update.OwnerName);
            if (index >= 0) Trips[index] = trip; else Trips.Add(trip);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
        {
            Trips.RemoveAll(t => ids.Contains(t.Id));
            return Task.CompletedTask;
        }

        public Task<bool> SetOccurrenceSkippedAsync(int id, DateOnly date, bool skipped, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task UpdateRouteAsync(int id, double distanceKm, double durationMinutes, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> SetLocationAsync(int id, string destination, RouteEstimate route, CancellationToken cancellationToken = default)
        {
            var index = Trips.FindIndex(t => t.Id == id);
            if (index < 0) return Task.FromResult(false);
            Trips[index] = Trips[index] with { Destination = destination, DistanceKm = route.DistanceKm, DurationMinutes = route.DurationMinutes,
                HasLocationOverride = Trips[index].IsFromCalendar };
            return Task.FromResult(true);
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
