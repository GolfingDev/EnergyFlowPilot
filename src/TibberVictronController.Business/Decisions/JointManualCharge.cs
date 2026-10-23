using System.Globalization;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>Operator request: add a fixed amount of stored energy to both batteries, regardless of price.</summary>
/// <param name="TargetKwh">Energy to add, measured as stored energy (SoC x capacity), not as grid import.</param>
/// <param name="MaximumPowerWatts">Optional total charge power limit; 0 means as fast as all limits allow.</param>
/// <param name="BaselineKwh">Stored energy of both batteries when charging began; null until the first live reading.</param>
public sealed record JointManualChargeRequest(decimal TargetKwh, int MaximumPowerWatts, DateTimeOffset ExpiresAtUtc, decimal? BaselineKwh);

public enum JointManualChargeOutcome { Charging, TargetReached, BatteriesFull, Expired }

public sealed record JointManualChargeStep(JointManualChargeOutcome Outcome, decimal ChargedKwh, JointBatteryCommand? Command, string Message);

/// <summary>
/// Turns a manual charge request into a joint charge command. Measuring progress from SoC x capacity keeps the
/// result meaningful across restarts and counts what is really available afterwards (losses excluded).
/// </summary>
public static class JointManualCharge
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static decimal StoredEnergyKwh(IEnumerable<JointBatteryUnit> batteries) =>
        batteries.Sum(b => b.Configuration.TotalCapacityKwh * b.State.StateOfChargePercent / 100m);

    public static JointManualChargeStep Evaluate(JointManualChargeRequest request, IReadOnlyList<JointBatteryUnit> batteries,
        DateTimeOffset nowUtc, DateTimeOffset commandValidUntilUtc)
    {
        var baseline = request.BaselineKwh ?? StoredEnergyKwh(batteries);
        var charged = Math.Max(0, StoredEnergyKwh(batteries) - baseline);
        var progress = string.Format(German, "{0:0.00} von {1:0.00} kWh", charged, request.TargetKwh);

        if (nowUtc >= request.ExpiresAtUtc)
            return new(JointManualChargeOutcome.Expired, charged, null, $"Manuelle Ladung nach Ablauf der Sicherheitsfrist beendet ({progress}).");
        if (charged >= request.TargetKwh)
            return new(JointManualChargeOutcome.TargetReached, charged, null, $"Manuelle Ladung abgeschlossen: {progress} geladen.");
        // Grid charging stops at the planning maximum, as agreed for manual charging as well.
        if (batteries.All(b => b.State.StateOfChargePercent >= b.Configuration.PlanningMaximumStateOfChargePercent))
            return new(JointManualChargeOutcome.BatteriesFull, charged, null, $"Manuelle Ladung beendet: beide Akkus haben ihr Planungsmaximum erreicht ({progress}).");

        // Request the sum of the batteries' current maxima (or the operator's limit) and let the allocator split it by
        // free headroom; the live limiter afterwards still enforces phase, AC-input and device limits.
        var available = batteries.Sum(b => Math.Min(b.Configuration.MaximumChargePowerWatts, Math.Max(0, b.AvailableChargeWatts ?? int.MaxValue)));
        var requested = request.MaximumPowerWatts > 0 ? Math.Min(available, request.MaximumPowerWatts) : available;
        var targets = JointBatteryAllocator.Allocate(batteries, requested, TimeSpan.FromMinutes(15));
        var command = new JointBatteryCommand(targets[0].SignedPowerWatts, targets[1].SignedPowerWatts, BatteryChargeSource.Grid,
            commandValidUntilUtc, $"Manuelle Ladung: {progress}.");
        return new(JointManualChargeOutcome.Charging, charged, command, command.Reason);
    }
}
