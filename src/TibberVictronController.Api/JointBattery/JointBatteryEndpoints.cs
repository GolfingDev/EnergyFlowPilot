using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

public static class JointBatteryEndpoints
{
    public static IEndpointRouteBuilder MapJointBatteryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/joint-battery/shadow", (JointBatteryRuntimeState state, JointBatteryExecutionState executionState, IUtcClock clock) =>
        {
            var current = state.Current;
            var expired = clock.UtcNow - current.UpdatedAtUtc > TimeSpan.FromSeconds(75) && current.Status != "Disabled";
            var plan = expired ? null : current.Plan;
            var local = current.LocalTelemetry;
            var execution = executionState.Current;
            var live = execution.Status is not ("Disabled" or "Shadow");
            var executionStale = execution.OwnsControl && clock.UtcNow - execution.UpdatedAtUtc > TimeSpan.FromSeconds(5);
            return TypedResults.Ok(new JointBatteryShadowDto(!live, executionStale ? "StaleExecution" : live ? execution.Status : expired ? "Stale" : current.Status,
                executionStale ? "Die Ausfuehrungsrueckmeldung ist veraltet. Angezeigte Sollwerte sind nur der letzte bekannte Stand." : live ? execution.Message : expired ? "Die gemeinsame Vorschau ist veraltet; es liegen keine gueltigen Sollwerte vor." : current.Message,
                current.UpdatedAtUtc,
                local is null ? null : new E3dcLocalDto(local.MeasuredAtUtc, local.SocPercent, local.BatteryPowerWatts,
                    local.UsableCapacityKwh, local.ReserveKwh, local.MaximumChargeWatts, local.MaximumDischargeWatts,
                    local.EffectiveChargeLimitWatts, local.EffectiveDischargeLimitWatts, local.OperatingMode, local.WeatherRegulationEnabled),
                plan?.CalculatedAtUtc, plan?.MaximumGridImportWatts,
                plan?.Warnings ?? [], plan?.Batteries.Select(b => new JointBatteryUnitDto(b.Id,
                    b.State.StateOfChargePercent, b.Configuration.TotalCapacityKwh,
                    b.Configuration.TotalCapacityKwh * b.Configuration.MinimumStateOfChargePercent / 100m,
                    b.Configuration.MaximumChargePowerWatts, b.Configuration.MaximumDischargePowerWatts,
                    b.MeasuredPowerWatts)).ToArray() ?? [],
                plan?.Slots.Select(s => new JointBatterySlotDto(s.TimeSlot.StartsAtUtc, s.TimeSlot.EndsAtUtc,
                    s.PricePerKwh, s.Currency, s.IsPriceEstimated, s.ChargeSource?.ToString() ?? "None", s.TargetLimited, s.Reason,
                    s.Targets.Select(t => new JointBatteryTargetDto(t.Id, t.SignedPowerWatts, t.SocBeforePercent, t.SocAfterPercent)).ToArray())).ToArray() ?? [],
                new(execution.Status, execution.UpdatedAtUtc, execution.OwnsControl, execution.VictronTargetWatts, execution.E3dcTargetWatts,
                    execution.VictronMeasuredWatts, execution.E3dcMeasuredWatts, execution.ValidUntilUtc)));
        }).WithTags("JointBattery");
        return endpoints;
    }
}

public sealed record JointBatteryShadowDto(bool IsShadow, string Status, string Message, DateTimeOffset UpdatedAtUtc,
    E3dcLocalDto? LocalTelemetry, DateTimeOffset? CalculatedAtUtc, int? MaximumGridImportWatts,
    IReadOnlyList<string> Warnings, IReadOnlyList<JointBatteryUnitDto> Batteries, IReadOnlyList<JointBatterySlotDto> Slots, JointBatteryExecutionDto Execution);
public sealed record JointBatteryExecutionDto(string Status, DateTimeOffset UpdatedAtUtc, bool OwnsControl,
    int VictronTargetWatts, int E3dcTargetWatts, int VictronMeasuredWatts, int E3dcMeasuredWatts, DateTimeOffset? ValidUntilUtc);
public sealed record E3dcLocalDto(DateTimeOffset MeasuredAtUtc, decimal SocPercent, int BatteryPowerWatts,
    decimal UsableCapacityKwh, decimal ReserveKwh, int MaximumChargeWatts, int MaximumDischargeWatts,
    int EffectiveChargeLimitWatts, int EffectiveDischargeLimitWatts, int OperatingMode, bool WeatherRegulationEnabled);
public sealed record JointBatteryUnitDto(string Id, decimal SocPercent, decimal CapacityKwh, decimal ReserveKwh,
    int MaximumChargeWatts, int MaximumDischargeWatts, int MeasuredPowerWatts);
public sealed record JointBatterySlotDto(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, decimal PricePerKwh,
    string Currency, bool IsPriceEstimated, string ChargeSource, bool TargetLimited, string Reason,
    IReadOnlyList<JointBatteryTargetDto> Targets);
public sealed record JointBatteryTargetDto(string Id, int SignedPowerWatts, decimal SocBeforePercent, decimal SocAfterPercent);
