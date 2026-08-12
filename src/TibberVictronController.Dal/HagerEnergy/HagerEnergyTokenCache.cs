namespace TibberVictronController.Dal.HagerEnergy;

/// <summary>
/// Singleton in-memory cache for the E3/DC access token.
/// Client Credentials tokens expire after 5 minutes — cache avoids unnecessary token requests.
/// </summary>
public sealed class HagerEnergyTokenCache
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);

    private string? cachedToken;
    private DateTimeOffset tokenExpiresAt = DateTimeOffset.MinValue;

    public string? GetCachedToken()
    {
        // Proactively treat the token as expired 30 seconds before its actual expiry
        return cachedToken is not null && DateTimeOffset.UtcNow < tokenExpiresAt.AddSeconds(-30)
            ? cachedToken
            : null;
    }

    public void StoreToken(string token, int expiresInSeconds)
    {
        cachedToken = token;
        tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);
    }
}
