using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

public interface IEvTripRepository
{
    Task<EvTrip> AddAsync(EvTripDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Trips whose departure (or known return) is not yet in the past, ordered by departure.</summary>
    Task<IReadOnlyList<EvTrip>> GetUpcomingAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

/// <summary>Geocoding and routing from home to a free-text destination.</summary>
public interface IRoutePlanner
{
    Task<RouteEstimate> EstimateAsync(GeoPoint origin, string destination, CancellationToken cancellationToken = default);
}
