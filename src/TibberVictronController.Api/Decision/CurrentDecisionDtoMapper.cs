using System.Text.Json;
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
            TryReadBatteryPowerWatts(logEntry.InputSummaryJson),
            logEntry.Reasons
                .Select(reason => new CurrentBatteryDecisionReasonDto(reason.RuleName, reason.Message))
                .ToArray())
        { Inputs = TryReadInputs(logEntry.InputSummaryJson) };
    }

    // Older entries lack DecisionParameters and invalid JSON yields no inputs; neither may break the log view.
    private static DecisionInputsDto? TryReadInputs(string inputSummaryJson)
    {
        try
        {
            using var document = JsonDocument.Parse(inputSummaryJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var parameters = root.TryGetProperty("DecisionParameters", out var p) && p.ValueKind == JsonValueKind.Object ? p : (JsonElement?)null;
            return new DecisionInputsDto(
                root.TryGetProperty("ElectricityPriceProvider", out var provider) && provider.ValueKind == JsonValueKind.String ? provider.GetString() : null,
                Int(root, "CurrentGridImportWatts"), Int(root, "CurrentPvProductionWatts"), Int(root, "CurrentBatteryPowerWatts"),
                Dec(parameters, "MinimumSavingsPerKwh"), Dec(parameters, "WearCostPerKwh"), Dec(parameters, "MinimumStateOfChargePercent"),
                Dec(parameters, "PlanningMinimumStateOfChargePercent"), Dec(parameters, "PlanningMaximumStateOfChargePercent"),
                parameters is { } pm ? Int(pm, "MaximumChargePowerWatts") : null, parameters is { } pd ? Int(pd, "MaximumDischargePowerWatts") : null,
                Dec(parameters, "RoundTripEfficiencyPercent"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? (int)Math.Round(number) : null;

    private static decimal? Dec(JsonElement? element, string name) =>
        element is { } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number : null;

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

    private static int? TryReadBatteryPowerWatts(string inputSummaryJson)
    {
        try
        {
            using var document = JsonDocument.Parse(inputSummaryJson);
            if (!document.RootElement.TryGetProperty("CurrentBatteryPowerWatts", out var property) ||
                property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
