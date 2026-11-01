using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

public interface IEvTripRepository
{
    Task<EvTrip> AddAsync(EvTripDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Trips whose departure (or known return) is not yet in the past, ordered by departure; includes ignored ones.</summary>
    Task<IReadOnlyList<EvTrip>> GetUpcomingAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    Task<EvTrip?> GetAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EvTrip>> GetCalendarTripsAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates the trip of one appointment; an operator's "ignore" survives the update.</summary>
    Task UpsertCalendarTripAsync(CalendarTripUpdate update, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    Task DeleteAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default);

    Task<bool> SetIgnoredAsync(int id, bool ignored, CancellationToken cancellationToken = default);

    Task<bool> SetOccurrenceSkippedAsync(int id, DateOnly date, bool skipped, CancellationToken cancellationToken = default);

    Task UpdateRouteAsync(int id, double distanceKm, double durationMinutes, CancellationToken cancellationToken = default);
}

/// <summary>Address suggestions while typing (search-as-you-type).</summary>
public interface IPlaceSearch
{
    Task<IReadOnlyList<PlaceSuggestion>> SuggestAsync(string query, GeoPoint? near, CancellationToken cancellationToken = default);
}

/// <summary>Appointments of the configured calendar.</summary>
public interface ICalendarSource
{
    Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);
}

/// <summary>Geocoding and routing from home to a free-text destination.</summary>
public interface IRoutePlanner
{
    /// <summary>placesOnly: accept only towns and villages (for places guessed from free text such as a subject).</summary>
    Task<RouteEstimate> EstimateAsync(GeoPoint origin, string destination, bool placesOnly = false, CancellationToken cancellationToken = default);

    /// <summary>Route to coordinates that are already known (a chosen suggestion or a stored trip).</summary>
    Task<RouteEstimate> RouteToAsync(GeoPoint origin, GeoPoint destination, string label, CancellationToken cancellationToken = default);
}
