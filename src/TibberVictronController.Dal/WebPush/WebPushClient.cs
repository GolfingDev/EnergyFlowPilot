using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.WebPush;

public enum WebPushOutcome
{
    Delivered,
    // The browser withdrew the subscription (uninstalled, permission revoked): delete it.
    Gone,
    Failed
}

public sealed record WebPushResult(WebPushOutcome Outcome, string? Problem = null);

public sealed record WebPushVapid(ECDsa PrivateKey, string PublicKey, string Subject);

/// <summary>Sends one encrypted message to a browser's push service (Google, Mozilla, ...).</summary>
public sealed class WebPushClient(HttpClient httpClient)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    public async Task<WebPushResult> SendAsync(WebPushSubscription subscription, byte[] payload, WebPushVapid vapid, TimeSpan timeToLive,
        DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        byte[] body;
        try
        {
            body = WebPushCrypto.Encrypt(payload, subscription.P256dh, subscription.Auth);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or CryptographicException)
        {
            // A broken subscription can never be delivered.
            return new WebPushResult(WebPushOutcome.Gone, exception.Message);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", ((int)timeToLive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", "high");
        request.Headers.TryAddWithoutValidation("Authorization",
            $"vapid t={WebPushCrypto.VapidToken(vapid.PrivateKey, subscription.Endpoint, vapid.Subject, nowUtc + TokenLifetime)}, k={vapid.PublicKey}");
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return new WebPushResult(WebPushOutcome.Delivered);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return new WebPushResult(WebPushOutcome.Gone, $"HTTP {(int)response.StatusCode}");
            return new WebPushResult(WebPushOutcome.Failed, $"Push-Dienst antwortet HTTP {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new WebPushResult(WebPushOutcome.Failed, $"Push-Dienst nicht erreichbar: {exception.Message}");
        }
    }
}
