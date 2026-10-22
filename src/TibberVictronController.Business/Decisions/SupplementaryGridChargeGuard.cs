using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>Shared live interlock: a grid charge must not drain the independently controlled E3/DC battery.</summary>
public sealed class SupplementaryGridChargeGuard
{
    public const int DischargeThresholdWatts = 50;
    public static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan RestartDelay = TimeSpan.FromMinutes(5);
    private readonly object syncRoot = new();
    private DateTimeOffset? blockedUntilUtc;

    public BatteryDecisionReason? Evaluate(CurrentSiteTelemetry telemetry, DateTimeOffset nowUtc)
    {
        lock (syncRoot)
        {
            var missing = telemetry.SupplementaryBatteryMonitoringRequired &&
                (telemetry.SupplementaryBatteryMeasuredAtUtc is not { } measured ||
                 measured > nowUtc.AddSeconds(30) || nowUtc - measured > MaximumTelemetryAge);
            if (missing)
            {
                blockedUntilUtc = nowUtc.Add(RestartDelay);
                return new(CurrentBatteryDecisionRuleIds.SupplementaryTelemetryMissing,
                    "Netzladung gesperrt: Fuer den konfigurierten E3/DC-Speicher fehlen aktuelle Batterie-Leistungswerte. Ohne diese Werte kann Umladen zwischen den Speichern nicht ausgeschlossen werden.");
            }
            if (telemetry.SupplementaryBatteryDischargeWatts > DischargeThresholdWatts)
            {
                blockedUntilUtc = nowUtc.Add(RestartDelay);
                return new(CurrentBatteryDecisionRuleIds.SupplementaryGridChargeBlocked,
                    $"Netzladung gesperrt: Der E3/DC-Akku entlaedt mit {telemetry.SupplementaryBatteryDischargeWatts} W. Der Victron darf diese Energie nicht als Netzladung aufnehmen.");
            }
            if (blockedUntilUtc is { } until && nowUtc < until)
                return new(CurrentBatteryDecisionRuleIds.SupplementaryGridChargeCooldown,
                    $"Netzladung bleibt bis {until:O} gesperrt (UTC). Nach E3/DC-Entladung oder fehlenden Messwerten verhindert eine fuenfminuetige Wiederanlaufsperre staendiges Ein- und Ausschalten.");
            return null;
        }
    }

    public static bool IsStopReason(string ruleId) => ruleId is
        CurrentBatteryDecisionRuleIds.SupplementaryGridChargeBlocked or
        CurrentBatteryDecisionRuleIds.SupplementaryGridChargeCooldown or
        CurrentBatteryDecisionRuleIds.SupplementaryTelemetryMissing or
        CurrentBatteryDecisionRuleIds.StaleSiteTelemetry or
        CurrentBatteryDecisionRuleIds.MissingSiteTelemetry or
        CurrentBatteryDecisionRuleIds.StaleBatteryState or
        CurrentBatteryDecisionRuleIds.MissingBatteryState or
        CurrentBatteryDecisionRuleIds.InvalidSiteTelemetry;
}
