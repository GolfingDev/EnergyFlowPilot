namespace TibberVictronController.Business.Models;

/// <summary>Last known car values from BMW CarData. Each value stays null until the car has reported it.</summary>
public sealed record CarSnapshot
{
    public static readonly CarSnapshot Empty = new();

    public string? Vin { get; init; }
    public double? StateOfChargePercent { get; init; }
    public DateTimeOffset? StateOfChargeAtUtc { get; init; }
    public double? TargetStateOfChargePercent { get; init; }
    public string? ChargingStatus { get; init; }
    public bool? IsPlugged { get; init; }
    // The plug value exactly as BMW sent it: its format is not documented, this shows what arrives (no personal data).
    public string? PluggedRawValue { get; init; }
    public double? RemainingRangeKm { get; init; }
    public double? OdometerKm { get; init; }
    public DateTimeOffset? LastMessageAtUtc { get; init; }
}

/// <summary>
/// What the CarData stream delivered, for diagnosis: message count, the descriptor names of the last message (no
/// values) and why a message was not used.
/// </summary>
public sealed record CarStreamDiagnostics(
    string? Subscription,
    int MessagesReceived,
    DateTimeOffset? LastMessageAtUtc,
    IReadOnlyList<string> LastDescriptors,
    string? LastProblem)
{
    public static readonly CarStreamDiagnostics Empty = new(null, 0, null, [], null);
}

/// <summary>Active queries of the REST API (used when the stream stays silent; BMW allows about 50 per day).</summary>
public sealed record CarPollDiagnostics(DateTimeOffset? LastPollAtUtc, int PollsToday, int DailyLimit, string? LastProblem, string? LastTrigger)
{
    public static readonly CarPollDiagnostics Empty = new(null, 0, 0, null, null);
}

public enum CarDataConnectionStatus
{
    NotConfigured,
    SignInRequired,
    SignInPending,
    Connecting,
    Connected,
    Error
}

/// <summary>State of the BMW CarData link, including an open device-code sign-in.</summary>
public sealed record CarDataConnectionState(
    CarDataConnectionStatus Status,
    string? Message,
    string? UserCode = null,
    string? VerificationUrl = null,
    DateTimeOffset? SignInExpiresAtUtc = null,
    DateTimeOffset? ConnectedSinceUtc = null)
{
    public static readonly CarDataConnectionState NotConfigured =
        new(CarDataConnectionStatus.NotConfigured, "Keine CarData-Client-ID eingetragen.");
}
