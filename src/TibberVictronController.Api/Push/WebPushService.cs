using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using TibberVictronController.Api.Authentication;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.WebPush;

namespace TibberVictronController.Api.Push;

/// <summary>Message for the service worker: shown as a notification, actions answer with the token.</summary>
public sealed record PushMessage(string Type, string Title, string Body, string Tag, string Url)
{
    public string? Token { get; init; }

    public IReadOnlyList<PushAction> Actions { get; init; } = [];
}

public sealed record PushAction(string Action, string Title);

/// <summary>
/// Push messages to the drivers' devices (operator 2026-11-03: Android, "charge now or by price?"). The VAPID key pair
/// is created on first use; its private key is stored encrypted with the data protection key ring.
/// </summary>
public sealed class WebPushService(
    IWebPushRepository repository,
    WebPushClient client,
    IDataProtectionProvider protection,
    ControllerAuthenticationOptions authentication,
    IUtcClock clock,
    ILogger<WebPushService> logger)
{
    private static readonly SemaphoreSlim KeyGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector protector = protection.CreateProtector("EnergyFlowPilot.WebPushVapid.v1");

    public async Task<string> GetPublicKeyAsync(CancellationToken cancellationToken) => (await KeysAsync(cancellationToken)).PublicKey;

    /// <summary>Sends to all devices (or those of one user); removes subscriptions the browser has withdrawn.</summary>
    public Task<(int Delivered, int Failed)> SendAsync(PushMessage message, string? userId, TimeSpan timeToLive, CancellationToken cancellationToken) =>
        SendAsync(_ => message, userId, timeToLive, cancellationToken);

    /// <summary>One message per device, e.g. with an answer token naming the device's user.</summary>
    public async Task<(int Delivered, int Failed)> SendAsync(Func<WebPushSubscription, PushMessage> messageFor, string? userId, TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        var subscriptions = (await repository.GetSubscriptionsAsync(cancellationToken)).Where(subscription => userId is null || subscription.UserId == userId).ToArray();
        if (subscriptions.Length == 0) return (0, 0);
        var keys = await KeysAsync(cancellationToken);
        using var privateKey = ECDsa.Create();
        privateKey.ImportPkcs8PrivateKey(Convert.FromBase64String(protector.Unprotect(keys.ProtectedPrivateKey)), out _);
        var vapid = new WebPushVapid(privateKey, keys.PublicKey, Subject());
        int delivered = 0, failed = 0;
        foreach (var subscription in subscriptions)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(messageFor(subscription), Json);
            var result = await client.SendAsync(subscription, payload, vapid, timeToLive, clock.UtcNow, cancellationToken);
            switch (result.Outcome)
            {
                case WebPushOutcome.Delivered:
                    delivered++;
                    break;
                case WebPushOutcome.Gone:
                    await repository.DeleteSubscriptionAsync(subscription.Endpoint, null, cancellationToken);
                    logger.LogInformation("Push-Abo von {User} entfernt: vom Browser zurueckgezogen ({Problem}).", subscription.UserName, result.Problem);
                    break;
                default:
                    failed++;
                    logger.LogWarning("Push an {User} fehlgeschlagen: {Problem}", subscription.UserName, result.Problem);
                    break;
            }
        }

        return (delivered, failed);
    }

    // The push services require a contact (RFC 8292); the public address of the installation is enough.
    private string Subject() => authentication.PublicBaseUrl is { Length: > 0 } url && url.StartsWith("https://", StringComparison.Ordinal)
        ? url.TrimEnd('/')
        : "mailto:energyflowpilot@localhost";

    private async Task<WebPushVapidKeys> KeysAsync(CancellationToken cancellationToken)
    {
        if (await repository.GetVapidKeysAsync(cancellationToken) is { } existing) return existing;
        await KeyGate.WaitAsync(cancellationToken);
        try
        {
            if (await repository.GetVapidKeysAsync(cancellationToken) is { } created) return created;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var keys = new WebPushVapidKeys(Base64Url.Encode(WebPushCrypto.PublicPoint(key.ExportParameters(false))),
                protector.Protect(Convert.ToBase64String(key.ExportPkcs8PrivateKey())));
            await repository.SaveVapidKeysAsync(keys, cancellationToken);
            logger.LogInformation("Push-Schluesselpaar (VAPID) erzeugt.");
            return keys;
        }
        finally
        {
            KeyGate.Release();
        }
    }
}

/// <summary>
/// Lets a notification answer the base state of charge question without a browser session (the sign-in cookie may have
/// expired hours ago): the token names the charging session and is valid for a few hours only.
/// </summary>
public sealed class BaseChoiceAnswerToken(IDataProtectionProvider protection)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
    private readonly ITimeLimitedDataProtector protector = protection.CreateProtector("EnergyFlowPilot.BaseChoiceAnswer.v1").ToTimeLimitedDataProtector();

    public string Create(int transactionId, string userName) => protector.Protect($"{transactionId}|{userName}", Lifetime);

    public (int TransactionId, string UserName)? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var parts = protector.Unprotect(token).Split('|', 2);
            return parts.Length == 2 && int.TryParse(parts[0], out var transactionId) ? (transactionId, parts[1]) : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
