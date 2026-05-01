using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfOperationalEventRepository : IOperationalEventRepository
{
    private readonly ControllerDbContext dbContext;

    public EfOperationalEventRepository(ControllerDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task SaveEventAsync(
        OperationalEvent operationalEvent,
        CancellationToken cancellationToken = default)
    {
        dbContext.OperationalEvents.Add(MapToEntity(operationalEvent));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperationalEvent>> GetRecentEventsAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), "Die Anzahl der Events muss groesser als 0 sein.");
        }

        return await dbContext.OperationalEvents
            .OrderByDescending(operationalEvent => operationalEvent.OccurredAtUtc)
            .Take(maxCount)
            .Select(operationalEvent => MapToDomain(operationalEvent))
            .ToArrayAsync(cancellationToken);
    }

    private static OperationalEventEntity MapToEntity(OperationalEvent operationalEvent)
    {
        return new OperationalEventEntity
        {
            Id = operationalEvent.Id,
            OccurredAtUtc = operationalEvent.OccurredAtUtc,
            Category = operationalEvent.Category,
            Severity = operationalEvent.Severity,
            Message = operationalEvent.Message,
            Details = operationalEvent.Details
        };
    }

    private static OperationalEvent MapToDomain(OperationalEventEntity operationalEvent)
    {
        return new OperationalEvent(
            operationalEvent.Id,
            operationalEvent.OccurredAtUtc,
            operationalEvent.Category,
            operationalEvent.Severity,
            operationalEvent.Message,
            operationalEvent.Details);
    }
}
