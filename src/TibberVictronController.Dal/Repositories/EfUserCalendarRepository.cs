using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfUserCalendarRepository(ControllerDbContext dbContext) : IUserCalendarRepository
{
    public async Task<IReadOnlyList<UserCalendar>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.UserCalendars.AsNoTracking().ToListAsync(cancellationToken)).Select(Map).ToArray();

    public async Task<UserCalendar?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.UserCalendars.AsNoTracking().FirstOrDefaultAsync(calendar => calendar.UserId == userId, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task SaveAsync(UserCalendar calendar, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.UserCalendars.FirstOrDefaultAsync(existing => existing.UserId == calendar.UserId, cancellationToken);
        if (entity is null)
        {
            entity = new UserCalendarEntity { UserId = calendar.UserId };
            dbContext.UserCalendars.Add(entity);
        }

        entity.Name = calendar.Name;
        entity.Mailbox = calendar.Mailbox;
        entity.ConnectedAtUtc = calendar.ConnectedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string userId, CancellationToken cancellationToken = default)
    {
        await dbContext.UserCalendars.Where(calendar => calendar.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }

    private static UserCalendar Map(UserCalendarEntity entity) => new(entity.UserId, entity.Name, entity.Mailbox, entity.ConnectedAtUtc);
}
