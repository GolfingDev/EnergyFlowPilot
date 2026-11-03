namespace TibberVictronController.Business.Models;

/// <summary>A browser's push subscription (one per device and user); keys as base64url, as the browser hands them out.</summary>
public sealed record WebPushSubscription(string Endpoint, string P256dh, string Auth, string UserId, string UserName, DateTimeOffset CreatedAtUtc);

/// <summary>The controller's VAPID key pair; the private key is stored only in protected form.</summary>
public sealed record WebPushVapidKeys(string PublicKey, string ProtectedPrivateKey);
