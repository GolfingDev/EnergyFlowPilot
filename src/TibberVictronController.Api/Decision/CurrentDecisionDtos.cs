namespace TibberVictronController.Api.Decision;

public sealed record CurrentBatteryDecisionReasonDto(
    string RuleId,
    string Message);

public sealed record DecisionLogEntryResponseDto(
    Guid Id,
    string DecisionState,
    string? ChargeSource,
    int TargetPowerWatts,
    DateTimeOffset DecidedAtUtc,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidToUtc,
    decimal? StateOfChargePercent,
    decimal? TibberPricePerKwh,
    string? TibberPriceCurrency,
    int? GridImportWatts,
    int? GridExportWatts,
    int? BatteryPowerWatts,
    IReadOnlyList<CurrentBatteryDecisionReasonDto> Reasons)
{
    /// <summary>Measured inputs and the thresholds in force at decision time; null parts were not logged yet.</summary>
    public DecisionInputsDto? Inputs { get; init; }
}

/// <summary>
/// Structured inputs of one logged decision. Thresholds are the configured values the engine used;
/// comparison prices computed inside the rules are not part of the log.
/// </summary>
public sealed record DecisionInputsDto(
    string? ElectricityPriceProvider,
    int? GridImportWatts,
    int? PvProductionWatts,
    int? BatteryPowerWatts,
    decimal? MinimumSavingsPerKwh,
    decimal? WearCostPerKwh,
    decimal? MinimumStateOfChargePercent,
    decimal? PlanningMinimumStateOfChargePercent,
    decimal? PlanningMaximumStateOfChargePercent,
    int? MaximumChargePowerWatts,
    int? MaximumDischargePowerWatts,
    decimal? RoundTripEfficiencyPercent);

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
