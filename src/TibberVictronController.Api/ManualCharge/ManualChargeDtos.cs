namespace TibberVictronController.Api.ManualCharge;

public sealed record ManualChargeRequestDto(
    int DurationMinutes,
    decimal PowerKw);

public sealed record ManualChargeStatusDto(
    bool IsActive,
    int PowerWatts,
    decimal PowerKw,
    DateTimeOffset? ExpiresAtUtc,
    int RemainingSeconds);

public sealed record ManualChargeErrorDto(string Message);
