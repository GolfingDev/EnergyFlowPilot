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
    // AC charging current per phase the car accepts, as set in the car (operator 2026-11-11: with a limit set the car
    // charged at about 4 kW although the wallbox offered 11 kW). The wallbox cannot see this, only the car reports it.
    public double? AcChargingLimitAmps { get; init; }
    public bool? AcChargingLimitActive { get; init; }
    // Quiet charging and AC restriction as BMW sends them; shown only, their effect on the power is not documented.
    public string? AcousticChargingLimit { get; init; }
    public string? AcRestriction { get; init; }
    public DateTimeOffset? LastMessageAtUtc { get; init; }

    // BMW's measurement time per value (key: value name). A query returns last known values that can be older than
    // what the stream already delivered; without these times an old value would overwrite a newer one.
    public IReadOnlyDictionary<string, DateTimeOffset>? ValueTimesUtc { get; init; }

    /// <summary>Energy charged into the battery since <see cref="StateOfChargeAtUtc"/>, measured by the wallbox.</summary>
    public double ChargedSinceSocKwh { get; init; }

    /// <summary>The reported state of charge plus what was charged since; what the planning works with.</summary>
    public double? StateOfChargeEstimatePercent(double capacityKwh) => StateOfChargePercent is { } soc
        ? Math.Min(100, soc + (capacityKwh > 0 ? ChargedSinceSocKwh / capacityKwh * 100 : 0))
        : null;

    /// <summary>
    /// Highest charging power the car currently accepts, or null when the car reports no limit. The current setting in
    /// the car counts, not past sessions: a limit changed today would not show in measured history.
    /// </summary>
    public double? ChargePowerLimitWatts(double wattsPerAmp) =>
        AcChargingLimitActive != false && AcChargingLimitAmps is { } amps && amps > 0 ? amps * wattsPerAmp : null;
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
