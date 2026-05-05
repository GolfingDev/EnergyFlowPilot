using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Forecast;

/// <summary>
/// Maps Battery Decision Engine forecast domain models to frontend-facing DTOs.
/// </summary>
public static class BatteryForecastDtoMapper
{
    /// <summary>
    /// Converts a forecast result into the DTO contract used by the frontend.
    /// </summary>
    public static BatteryForecastResponseDto Map(BatteryForecastResult forecastResult)
    {
        if (forecastResult is null)
        {
            throw new ArgumentNullException(nameof(forecastResult), "Der Forecast darf nicht null sein.");
        }

        var entries = forecastResult.Entries
            .Select(MapEntry)
            .ToArray();

        return new BatteryForecastResponseDto(
            forecastResult.InitialBatteryState.StateOfChargePercent,
            forecastResult.BatteryConfiguration.TotalCapacityKwh,
            entries);
    }

    private static BatteryForecastEntryDto MapEntry(BatteryForecastEntry entry)
    {
        return new BatteryForecastEntryDto(
            entry.TimeSlot.StartsAtUtc,
            entry.TimeSlot.EndsAtUtc,
            entry.TibberPricePerKwh,
            entry.TibberPriceCurrency,
            entry.ExpectedPvYieldKwh,
            entry.ExpectedConsumptionKwh,
            entry.ExpectedGridImportBeforeBatteryKwh,
            entry.StateOfChargeBeforePercent,
            entry.StateOfChargeAfterPercent,
            entry.Decision.Instruction.DecisionState.ToString(),
            entry.Decision.Instruction.ChargeSource?.ToString(),
            entry.Decision.TargetPowerWatts,
            entry.Reasons.Select(reason => new BatteryDecisionReasonDto(reason.RuleName, reason.Message)).ToArray());
    }
}
