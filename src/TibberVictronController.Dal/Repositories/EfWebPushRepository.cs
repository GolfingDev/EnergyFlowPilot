using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfWebPushRepository(ControllerDbContext dbContext) : IWebPushRepository
{
    private const int KeyRowId = 1;

    public async Task<IReadOnlyList<WebPushSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.WebPushSubscriptions.AsNoTracking().ToListAsync(cancellationToken))
            .Select(entity => new WebPushSubscription(entity.Endpoint, entity.P256dh, entity.Auth, entity.UserId, entity.UserName, entity.CreatedAtUtc))
            .ToArray();

    public async Task SaveSubscriptionAsync(WebPushSubscription subscription, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.WebPushSubscriptions.FirstOrDefaultAsync(existing => existing.Endpoint == subscription.Endpoint, cancellationToken);
        if (entity is null)
        {
            entity = new WebPushSubscriptionEntity { Endpoint = subscription.Endpoint };
            dbContext.WebPushSubscriptions.Add(entity);
        }

        entity.P256dh = subscription.P256dh;
        entity.Auth = subscription.Auth;
        entity.UserId = subscription.UserId;
        entity.UserName = subscription.UserName;
        entity.CreatedAtUtc = subscription.CreatedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteSubscriptionAsync(string endpoint, string? userId, CancellationToken cancellationToken = default)
    {
        var deleted = await dbContext.WebPushSubscriptions
            .Where(subscription => subscription.Endpoint == endpoint && (userId == null || subscription.UserId == userId))
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    public async Task<WebPushVapidKeys?> GetVapidKeysAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.WebPushKeys.AsNoTracking().FirstOrDefaultAsync(key => key.Id == KeyRowId, cancellationToken);
        return entity is null ? null : new WebPushVapidKeys(entity.PublicKey, entity.ProtectedPrivateKey);
    }

    public async Task SaveVapidKeysAsync(WebPushVapidKeys keys, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.WebPushKeys.FirstOrDefaultAsync(key => key.Id == KeyRowId, cancellationToken);
        if (entity is null)
        {
            entity = new WebPushKeyEntity { Id = KeyRowId };
            dbContext.WebPushKeys.Add(entity);
        }

        entity.PublicKey = keys.PublicKey;
        entity.ProtectedPrivateKey = keys.ProtectedPrivateKey;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
