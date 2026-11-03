using TibberVictronController.Api.EvCharging;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Dashboard;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Automations;

/// <summary>Maps the automation status endpoint; collecting and mapping live in the service and the builder.</summary>
public static class AutomationEndpoints
{
    public static IEndpointRouteBuilder MapAutomationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/automations", GetAutomationsAsync)
            .WithName("GetAutomations")
            .WithTags("Automations");
        return endpoints;
    }

    public static async Task<IResult> GetAutomationsAsync(AutomationStatusService service, CancellationToken cancellationToken)
    {
        var automations = await service.GetAsync(cancellationToken);
        return TypedResults.Ok(automations.Select(a => new AutomationStatusDto(a.Id, a.Title, a.Enabled, a.State, a.Action, a.Trigger,
            a.Devices, a.NextChangeAtUtc, a.NextChange, a.RuleId, a.SettingKey, a.SettingInverted, a.Problem)).ToArray());
    }
}

public sealed record AutomationStatusDto(string Id, string Title, bool? Enabled, string State, string Action, string? Trigger,
    string Devices, DateTimeOffset? NextChangeAtUtc, string? NextChange, string? RuleId, string SettingKey, bool SettingInverted, string? Problem);

/// <summary>
/// Collects the states the controller already holds for battery optimisation, car charging and hot water.
/// Only reads: no service is triggered to recalculate or to send anything.
/// </summary>
public sealed class AutomationStatusService(IServiceProvider services)
{
    public async Task<IReadOnlyList<AutomationStatus>> GetAsync(CancellationToken cancellationToken)
    {
        var settings = services.GetRequiredService<IControllerSettingStore>();
        var now = services.GetRequiredService<IUtcClock>().UtcNow;

        DashboardSummary? summary = null;
        try
        {
            summary = await services.GetRequiredService<IDashboardSummaryService>().GetSummaryAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Without a summary the battery automation is still listed, only without the current reason.
        }

        var execution = services.GetRequiredService<JointBatteryExecutionState>().Current;
        var jointActive = execution.Status is not ("Disabled" or "Shadow");
        var battery = new BatteryAutomationInput(summary, await FlagAsync(settings, ControllerSettingDefaults.VictronDryRunKey, true, cancellationToken),
            jointActive, jointActive ? execution.Status : null);

        EvAutomationInput? ev = null;
        if (await FlagAsync(settings, ControllerSettingDefaults.EvOcppEnabledKey, false, cancellationToken))
        {
            var control = services.GetRequiredService<EvChargingControlState>().Current;
            ev = new EvAutomationInput(control.Enabled, control.Mode, control.Amps, control.Reason, control.CommandProblem,
                services.GetRequiredService<EvChargeLoadForecast>().NextPlannedStartUtc(now));
        }

        HotWaterAutomationInput? hotWater = null;
        if (await FlagAsync(settings, ControllerSettingDefaults.WeishauptBiblockModbusEnabledKey, false, cancellationToken))
        {
            var state = await services.GetRequiredService<IWeishauptHotWaterStateStore>().LoadAsync(cancellationToken);
            hotWater = new HotWaterAutomationInput(await FlagAsync(settings, ControllerSettingDefaults.WeishauptHotWaterEnabledKey, false, cancellationToken),
                state.OriginalCelsius.HasValue, state.Suspended, state.LastPlan);
        }

        return AutomationStatusBuilder.Build(new AutomationStatusInput(now, battery, ev, hotWater));
    }

    // Same reading as the workers: a missing or unreadable flag falls back to the safe default.
    private static async Task<bool> FlagAsync(IControllerSettingStore settings, string key, bool fallback, CancellationToken cancellationToken)
    {
        var setting = await settings.GetSettingAsync(key, cancellationToken);
        return setting is { IsConfigured: true } && bool.TryParse(setting.Value, out var value) ? value : fallback;
    }
}
