using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfEvTripRepository(ControllerDbContext dbContext) : IEvTripRepository
{
    public async Task<EvTrip> AddAsync(EvTripDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
    {
        var entity = new EvTripEntity
        {
            DepartureAtUtc = draft.DepartureAtUtc,
            ReturnAtUtc = draft.ReturnAtUtc,
            Destination = draft.Destination,
            ResolvedAddress = draft.ResolvedAddress.Length <= 500 ? draft.ResolvedAddress : draft.ResolvedAddress[..500],
            Latitude = draft.Latitude,
            Longitude = draft.Longitude,
            DistanceKm = draft.DistanceKm,
            DurationMinutes = draft.DurationMinutes,
            CreatedAtUtc = createdAtUtc,
            RecurrenceDays = draft.RecurrenceDays,
            RecurrenceUntilUtc = draft.RecurrenceUntilUtc
        };
        dbContext.EvTrips.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EvTrips.FirstOrDefaultAsync(trip => trip.Id == id, cancellationToken);
        if (entity is null) return false;
        dbContext.EvTrips.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<EvTrip>> GetUpcomingAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        // A trip without a known return stays listed for a day after departure, so a running trip is still visible.
        // Series stay until their end date.
        var dayAgo = nowUtc.AddDays(-1);
        var entities = await dbContext.EvTrips.AsNoTracking()
            .Where(trip => (trip.RecurrenceDays != 0 && (trip.RecurrenceUntilUtc == null || trip.RecurrenceUntilUtc > nowUtc)) ||
                           (trip.RecurrenceDays == 0 && trip.ReturnAtUtc != null && trip.ReturnAtUtc > nowUtc) ||
                           (trip.RecurrenceDays == 0 && trip.ReturnAtUtc == null && trip.DepartureAtUtc > dayAgo))
            .OrderBy(trip => trip.DepartureAtUtc)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async Task<EvTrip?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EvTrips.AsNoTracking().FirstOrDefaultAsync(trip => trip.Id == id, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<EvTrip>> GetCalendarTripsAsync(CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.EvTrips.AsNoTracking()
            .Where(trip => trip.Source == EvTripSources.Calendar)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async Task UpsertCalendarTripAsync(CalendarTripUpdate update, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EvTrips.FirstOrDefaultAsync(
            trip => trip.Source == EvTripSources.Calendar && trip.ExternalId == update.ExternalId, cancellationToken);
        if (entity is null)
        {
            // Without a route there is nothing to create the trip from.
            if (update.Route is null) return;
            entity = new EvTripEntity { Source = EvTripSources.Calendar, ExternalId = update.ExternalId, CreatedAtUtc = nowUtc };
            dbContext.EvTrips.Add(entity);
        }

        entity.Subject = update.Subject;
        entity.Destination = update.Destination;
        entity.DepartureAtUtc = update.DepartureAtUtc;
        entity.ReturnAtUtc = update.ReturnAtUtc;
        if (update.Route is { } route)
        {
            entity.ResolvedAddress = route.ResolvedAddress.Length <= 500 ? route.ResolvedAddress : route.ResolvedAddress[..500];
            entity.Latitude = route.Destination.Latitude;
            entity.Longitude = route.Destination.Longitude;
            entity.DistanceKm = route.DistanceKm;
            entity.DurationMinutes = route.DurationMinutes;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.EvTrips.Where(trip => ids.Contains(trip.Id)).ToListAsync(cancellationToken);
        dbContext.EvTrips.RemoveRange(entities);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> SetIgnoredAsync(int id, bool ignored, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EvTrips.FirstOrDefaultAsync(trip => trip.Id == id, cancellationToken);
        if (entity is null) return false;
        entity.IsIgnored = ignored;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static EvTrip Map(EvTripEntity entity) => new(entity.Id, entity.DepartureAtUtc, entity.ReturnAtUtc, entity.Destination,
        entity.ResolvedAddress, entity.DistanceKm, entity.DurationMinutes, entity.CreatedAtUtc, entity.RecurrenceDays, entity.RecurrenceUntilUtc,
        entity.Source, entity.ExternalId, entity.Subject, entity.IsIgnored);
}
