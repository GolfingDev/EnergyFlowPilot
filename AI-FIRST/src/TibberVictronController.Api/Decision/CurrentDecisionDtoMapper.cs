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
}
