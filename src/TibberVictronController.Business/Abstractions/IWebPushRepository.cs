using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

public interface IWebPushRepository
{
    Task<IReadOnlyList<WebPushSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces the subscription with the same endpoint.</summary>
    Task SaveSubscriptionAsync(WebPushSubscription subscription, CancellationToken cancellationToken = default);

    /// <summary>Removes a subscription; with a user id only if it belongs to that user.</summary>
    Task<bool> DeleteSubscriptionAsync(string endpoint, string? userId, CancellationToken cancellationToken = default);

    Task<WebPushVapidKeys?> GetVapidKeysAsync(CancellationToken cancellationToken = default);

    Task SaveVapidKeysAsync(WebPushVapidKeys keys, CancellationToken cancellationToken = default);
}
