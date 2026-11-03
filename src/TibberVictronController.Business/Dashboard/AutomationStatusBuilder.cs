using System.Globalization;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Dashboard;

/// <summary>One automation as the user sees it: what it does now, why, on which devices and what comes next.</summary>
public sealed record AutomationStatus(
    string Id,
    string Title,
    bool? Enabled,
    string State,
    string Action,
    string? Trigger,
    string Devices,
    DateTimeOffset? NextChangeAtUtc,
    string? NextChange,
    string? RuleId,
    string SettingKey,
    bool SettingInverted)
{
    /// <summary>A command or device problem the user should see next to the automation.</summary>
    public string? Problem { get; init; }
}

public sealed record BatteryAutomationInput(DashboardSummary? Summary, bool DryRun, bool JointControlActive, string? JointStatus);

public sealed record EvAutomationInput(bool Enabled, EvChargingMode Mode, double? Amps, string Reason, string? CommandProblem,
    DateTimeOffset? NextPlannedChargeStartsAtUtc);

public sealed record HotWaterAutomationInput(bool Enabled, bool IsRaised, bool IsSuspended, WeishauptHotWaterPricePlan? Plan);

/// <summary>Everything the builder needs; a missing device (null) means the automation is not set up and not listed.</summary>
public sealed record AutomationStatusInput(DateTimeOffset NowUtc, BatteryAutomationInput Battery, EvAutomationInput? Ev, HotWaterAutomationInput? HotWater);

/// <summary>
/// Builds the automation list from states the controller already holds. It explains, it does not decide:
/// every reason is a text the planner or a control service produced; nothing here changes control behaviour.
/// </summary>
public static class AutomationStatusBuilder
{
    public static IReadOnlyList<AutomationStatus> Build(AutomationStatusInput input)
    {
        var list = new List<AutomationStatus> { Battery(input.Battery) };
        if (input.Ev is { } ev) list.Add(Ev(ev));
        if (input.HotWater is { } hotWater) list.Add(HotWater(hotWater, input.NowUtc));
        return list;
    }

    public static string CategoryLabel(DashboardActionCategory category) => category switch
    {
        DashboardActionCategory.Discharge => "Akku deckt Verbrauch",
        DashboardActionCategory.PvCharge => "PV lädt",
        DashboardActionCategory.GridCharge => "Netzladung",
        DashboardActionCategory.Hold => "Energie zurückhalten",
        DashboardActionCategory.Full => "Akku voll",
        _ => "Ruhe"
    };

    private static AutomationStatus Battery(BatteryAutomationInput input)
    {
        var overview = input.Summary?.Overview;
        var current = input.Summary?.Current;
        var category = overview?.Category ?? current?.Category;
        var state = input.DryRun ? "Simulation – keine Befehle an die Hardware"
            : input.JointControlActive ? $"Gemeinsame Steuerung: {JointStatusLabel(input.JointStatus)}"
            : "Steuert Akku 1 (Victron)";
        return new AutomationStatus("battery", "Akku-Optimierung nach Preis & PV", !input.DryRun, state,
            category is { } c ? CategoryLabel(c) : "Noch keine Entscheidung",
            overview?.Reason ?? current?.Reason,
            input.JointControlActive ? "Akku 1 (Victron) und Akku 2 (E3/DC)" : "Akku 1 (Victron)",
            overview?.Next?.StartsAtUtc, overview?.Next is { } next ? CategoryLabel(next.Category) : null,
            current?.RuleId, ControllerSettingDefaults.VictronDryRunKey, SettingInverted: true);
    }

    private static AutomationStatus Ev(EvAutomationInput input)
    {
        var mode = EvModeLabel(input.Mode);
        return new AutomationStatus("ev", "E-Auto-Laden nach Plan", input.Enabled,
            input.Enabled ? mode : "Automatik aus – nur Vorschau",
            input.Amps is { } amps ? $"{mode} · {amps.ToString("0.#", CultureInfo.GetCultureInfo("de-DE"))} A" : mode,
            input.Reason, "Wallbox und E-Auto",
            input.NextPlannedChargeStartsAtUtc, input.NextPlannedChargeStartsAtUtc is null ? null : "Geplante Ladung",
            null, ControllerSettingDefaults.EvChargingControlEnabledKey, SettingInverted: false)
        { Problem = input.CommandProblem };
    }

    private static AutomationStatus HotWater(HotWaterAutomationInput input, DateTimeOffset now)
    {
        var plan = input.Plan;
        // The next change is the start of a coming window, or its end while the set point is raised.
        var (nextAt, next) = plan?.StartsAtUtc is { } start && start > now ? (start, "Preisfenster beginnt")
            : input.IsRaised && plan?.EndsAtUtc is { } end && end > now ? (end, "Preisfenster endet")
            : ((DateTimeOffset?)null, (string?)null);
        return new AutomationStatus("hotWater", "Warmwasser im Preisfenster", input.Enabled,
            !input.Enabled ? "Aus" : input.IsSuspended ? "Ausgesetzt" : input.IsRaised ? "Anhebung aktiv" : "Bereit",
            input.IsRaised ? "Sollwert angehoben" : "Normaler Sollwert",
            plan?.Reason, "Wärmepumpe (Warmwasser)", nextAt, next, null,
            ControllerSettingDefaults.WeishauptHotWaterEnabledKey, SettingInverted: false);
    }

    private static string EvModeLabel(EvChargingMode mode) => mode switch
    {
        EvChargingMode.Off => "Automatik aus",
        EvChargingMode.Waiting => "Wartet",
        EvChargingMode.Plan => "Lädt nach Plan",
        EvChargingMode.PvSurplus => "Lädt mit PV-Überschuss",
        EvChargingMode.Paused => "Pausiert",
        EvChargingMode.FullPowerFallback => "Volle Leistung (Notbetrieb)",
        _ => mode.ToString()
    };

    private static string JointStatusLabel(string? status) => status switch
    {
        "Active" => "Aktiv", "Starting" => "Startet", "Transition" => "Wechsel", "Monitoring" => "Beobachtung",
        "Shadow" => "Nur Vorschau", "Blocked" => "Wartet", "Disabled" => "Aus", "Restoring" => "Rücknahme läuft",
        "Stale" => "Vorschau veraltet", "StaleExecution" => "Rückmeldung veraltet", "RecoveryPending" => "Rücknahme ausstehend",
        null => "unbekannt",
        _ => status
    };
}
