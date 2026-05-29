using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Decision;

public static class CurrentDecisionDtoMapper
{
    public static CurrentBatteryDecisionResponseDto Map(CurrentBatteryDecisionResult result)
    {
        return new CurrentBatteryDecisionResponseDto(
            result.Decision.Instruction.DecisionState.ToString(),
            result.Decision.Instruction.ChargeSource?.ToString(),
            result.Decision.TargetPowerWatts,
            result.DecidedAtUtc,
            result.ValidFromUtc,
            result.ValidToUtc,
            result.BatteryState.StateOfChargePercent,
            result.SiteTelemetry.CurrentGridImportWatts,
            result.SiteTelemetry.CurrentPvProductionWatts,
            result.TibberPricePerKwh,
            result.TibberPriceCurrency,
            result.Reasons
                .Select(reason => new CurrentBatteryDecisionReasonDto(reason.RuleName, reason.Message))
                .ToArray());
    }

    public static DecisionLogEntryResponseDto Map(DecisionLogEntry logEntry)
    {
        return new DecisionLogEntryResponseDto(
            logEntry.Id,
            logEntry.Decision.Instruction.DecisionState.ToString(),
            logEntry.Decision.Instruction.ChargeSource?.ToString(),
            logEntry.Decision.TargetPowerWatts,
            logEntry.DecidedAtUtc,
            logEntry.ValidFromUtc,
            logEntry.ValidToUtc,
            logEntry.StateOfChargePercent,
            logEntry.TibberPricePerKwh,
            logEntry.TibberPriceCurrency,
            logEntry.GridImportWatts,
            logEntry.GridExportWatts,
            logEntry.Reasons
                .Select(reason => new CurrentBatteryDecisionReasonDto(reason.RuleName, reason.Message))
                .ToArray());
    }

    public static CurrentBatteryDecisionResponseDto MapCurrent(DecisionLogEntry logEntry)
    {
        return new CurrentBatteryDecisionResponseDto(
            logEntry.Decision.Instruction.DecisionState.ToString(),
            logEntry.Decision.Instruction.ChargeSource?.ToString(),
            logEntry.Decision.TargetPowerWatts,
            logEntry.DecidedAtUtc,
            logEntry.ValidFromUtc,
            logEntry.ValidToUtc,
            logEntry.StateOfChargePercent ?? 0m,
            (logEntry.GridImportWatts ?? 0) - (logEntry.GridExportWatts ?? 0),
            CurrentPvProductionWatts: 0,
            logEntry.TibberPricePerKwh,
            logEntry.TibberPriceCurrency,
            logEntry.Reasons
                .Select(reason => new CurrentBatteryDecisionReasonDto(reason.RuleName, reason.Message))
                .ToArray());
    }
}
