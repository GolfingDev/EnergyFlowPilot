namespace TibberVictronController.Api.Forecast;

/// <summary>
/// Frontend DTO for a complete Battery Decision Engine forecast.
/// </summary>
public sealed record BatteryForecastResponseDto(
    decimal InitialStateOfChargePercent,
    decimal BatteryTotalCapacityKwh,
    IReadOnlyList<BatteryForecastEntryDto> Entries);

/// <summary>
/// Frontend DTO for one forecast slot.
/// </summary>
public sealed record BatteryForecastEntryDto(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    decimal TibberPricePerKwh,
    string TibberPriceCurrency,
    decimal ExpectedPvYieldKwh,
    decimal ExpectedConsumptionKwh,
    decimal ExpectedGridImportBeforeBatteryKwh,
    decimal StateOfChargeBeforePercent,
    decimal StateOfChargeAfterPercent,
    string DecisionState,
    string? ChargeSource,
    int TargetPowerWatts,
    IReadOnlyList<BatteryDecisionReasonDto> Reasons);

/// <summary>
/// Frontend DTO for a structured Decision Engine reason.
/// </summary>
public sealed record BatteryDecisionReasonDto(string RuleName, string Message);

/// <summary>
/// Frontend DTO for forecast request validation errors.
/// </summary>
public sealed record ForecastErrorDto(string Message);
