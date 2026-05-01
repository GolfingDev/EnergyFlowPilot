namespace TibberVictronController.Business.Auditing;

/// <summary>
/// Checks technical invariants that must hold for every audited Decision Engine forecast.
/// </summary>
public static class DecisionAuditInvariantValidator
{
    private const decimal Tolerance = 0.0001m;

    /// <summary>
    /// Returns all invariant violations in a readable German form.
    /// </summary>
    public static IReadOnlyList<string> Validate(DecisionAuditReport report)
    {
        var violations = new List<string>();

        ValidateSlotCount(report, violations);
        ValidateTimeGrid(report, violations);
        ValidateDecisionReasons(report, violations);
        ValidateStateOfChargeBoundaries(report, violations);
        ValidateChargeAndDischargeBoundaries(report, violations);
        ValidatePhysicalPowerBoundaries(report, violations);

        return violations;
    }

    private static void ValidateSlotCount(DecisionAuditReport report, List<string> violations)
    {
        if (report.DecisionSlots.Count != 96)
        {
            violations.Add($"Anzahl Slots fuer 24h ist {report.DecisionSlots.Count}, erwartet sind 96.");
        }
    }

    private static void ValidateTimeGrid(DecisionAuditReport report, List<string> violations)
    {
        for (var index = 0; index < report.DecisionSlots.Count; index++)
        {
            var slot = report.DecisionSlots[index];

            if (!slot.TimeSlot.IsFifteenMinuteSlot)
            {
                violations.Add($"Zeitraster ist nicht 15 Minuten im Slot {slot.TimeSlot.StartsAtUtc:O}.");
            }

            if (index > 0 && report.DecisionSlots[index - 1].TimeSlot.EndsAtUtc != slot.TimeSlot.StartsAtUtc)
            {
                violations.Add($"Zeitraster ist nicht durchgehend vor Slot {slot.TimeSlot.StartsAtUtc:O}.");
            }
        }
    }

    private static void ValidateDecisionReasons(DecisionAuditReport report, List<string> violations)
    {
        foreach (var slot in report.DecisionSlots)
        {
            if (string.IsNullOrWhiteSpace(slot.Reason) || string.IsNullOrWhiteSpace(slot.RuleId))
            {
                violations.Add($"Jeder DecisionSlot braucht RuleId und Reason. Fehler im Slot {slot.TimeSlot.StartsAtUtc:O}.");
            }
        }
    }

    private static void ValidateStateOfChargeBoundaries(DecisionAuditReport report, List<string> violations)
    {
        var minSoc = report.Scenario.BatteryConfiguration.MinimumStateOfChargePercent;
        const decimal maxSoc = 100m;

        foreach (var slot in report.DecisionSlots)
        {
            if (slot.ExpectedSocPercent < minSoc - Tolerance || slot.ExpectedSocPercent > maxSoc + Tolerance)
            {
                violations.Add($"ExpectedSocPercent liegt ausserhalb von MinSocPercent und MaxSocPercent im Slot {slot.TimeSlot.StartsAtUtc:O}.");
            }
        }
    }

    private static void ValidateChargeAndDischargeBoundaries(DecisionAuditReport report, List<string> violations)
    {
        var minSoc = report.Scenario.BatteryConfiguration.MinimumStateOfChargePercent;
        const decimal maxSoc = 100m;

        foreach (var slot in report.DecisionSlots)
        {
            if (slot.Action == "Discharge" && slot.StateOfChargeBeforePercent <= minSoc + Tolerance)
            {
                violations.Add($"Kein Discharge, wenn SoC <= MinSocPercent. Fehler im Slot {slot.TimeSlot.StartsAtUtc:O}.");
            }

            if ((slot.Action == "ChargeFromGrid" || slot.Action == "ChargeFromPV") &&
                slot.StateOfChargeBeforePercent >= maxSoc - Tolerance)
            {
                violations.Add($"Kein Charge, wenn SoC >= MaxSocPercent. Fehler im Slot {slot.TimeSlot.StartsAtUtc:O}.");
            }
        }
    }

    private static void ValidatePhysicalPowerBoundaries(DecisionAuditReport report, List<string> violations)
    {
        var batteryConfiguration = report.Scenario.BatteryConfiguration;
        var maxChargeSocDelta = CalculateMaximumSocDeltaPercent(
            batteryConfiguration.MaximumChargePowerWatts,
            batteryConfiguration.TotalCapacityKwh);
        var maxDischargeSocDelta = CalculateMaximumSocDeltaPercent(
            batteryConfiguration.MaximumDischargePowerWatts,
            batteryConfiguration.TotalCapacityKwh);

        foreach (var slot in report.DecisionSlots)
        {
            var socDelta = Math.Abs(slot.ExpectedSocPercent - slot.StateOfChargeBeforePercent);

            if ((slot.Action == "ChargeFromGrid" || slot.Action == "ChargeFromPV") &&
                socDelta > maxChargeSocDelta + Tolerance)
            {
                violations.Add($"SoC-Fortschreibung ueberschreitet plausible Ladegrenzen im Slot {slot.TimeSlot.StartsAtUtc:O}.");
            }

            if (slot.Action == "Discharge" && socDelta > maxDischargeSocDelta + Tolerance)
            {
                violations.Add($"SoC-Fortschreibung ueberschreitet plausible Entladegrenzen im Slot {slot.TimeSlot.StartsAtUtc:O}.");
            }
        }
    }

    private static decimal CalculateMaximumSocDeltaPercent(int powerWatts, decimal totalCapacityKwh)
    {
        var slotEnergyKwh = powerWatts / 1000m * 0.25m;

        return slotEnergyKwh / totalCapacityKwh * 100m;
    }
}
