namespace TibberVictronController.Api.Decision;

public sealed record CurrentBatteryDecisionReasonDto(
    string RuleId,
    string Message);

public sealed record CurrentBatteryDecisionResponseDto(
    string DecisionState,
    string? ChargeSource,
    int TargetPowerWatts,
    DateTimeOffset DecidedAtUtc,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidToUtc,
    decimal StateOfChargePercent,
    int CurrentGridImportWatts,
    int CurrentPvProductionWatts,
    decimal? TibberPricePerKwh,
    string? TibberPriceCurrency,
    IReadOnlyList<CurrentBatteryDecisionReasonDto> Reasons);
