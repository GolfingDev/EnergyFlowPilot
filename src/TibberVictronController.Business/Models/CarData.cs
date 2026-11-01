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
    public double? RemainingRangeKm { get; init; }
    public double? OdometerKm { get; init; }
    public DateTimeOffset? LastMessageAtUtc { get; init; }
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
