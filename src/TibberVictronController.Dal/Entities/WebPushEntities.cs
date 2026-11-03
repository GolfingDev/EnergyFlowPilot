namespace TibberVictronController.Dal.Entities;

public sealed class WebPushSubscriptionEntity
{
    public int Id { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class WebPushKeyEntity
{
    public int Id { get; set; }
    public string PublicKey { get; set; } = string.Empty;
    public string ProtectedPrivateKey { get; set; } = string.Empty;
}
