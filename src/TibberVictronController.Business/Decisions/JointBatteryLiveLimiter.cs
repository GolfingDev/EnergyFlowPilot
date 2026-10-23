using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>Rebounds the plan against present energy, measured house load, device and phase limits.</summary>
public static class JointBatteryLiveLimiter
{
    public static JointBatteryCommand Limit(JointBatteryCommand desired, JointBatteryLiveSnapshot live, DateTimeOffset now)
    {
        var limits = live.Limits;
        if (live.Batteries.Count != 2 || now >= desired.ValidUntilUtc || now - live.MeasuredAtUtc > TimeSpan.FromSeconds(5) || live.MeasuredAtUtc > now.AddSeconds(2) ||
            live.Batteries.Any(b => now - b.State.MeasuredAtUtc > TimeSpan.FromSeconds(5) || b.State.MeasuredAtUtc > now.AddSeconds(2)))
            throw new InvalidOperationException("Gemeinsamer Sollwert oder Messwerte sind abgelaufen.");
        if (limits.PhaseImportWatts.Count != 3 || limits.PhaseImportWatts.Any(p => p <= 0) || limits.VictronAcInputWatts <= 0 ||
            limits.VictronPhaseCount is < 1 or > 3 || live.PhaseGridWatts.Count != 3 || live.AcOutWatts.Count != limits.VictronPhaseCount)
            throw new InvalidOperationException("Bestaetigte Anschluss- und Phasengrenzen fehlen.");
        if (Math.Sign(desired.VictronWatts) * Math.Sign(desired.E3dcWatts) < 0)
            throw new InvalidOperationException("Gegenlaeufige Akku-Sollwerte sind unzulaessig.");
        if (live.PhaseGridWatts.Sum() < -150 && live.Batteries.Any(b => b.MeasuredPowerWatts < -150))
            return desired with { VictronWatts = 0, E3dcWatts = 0, Reason = "Akkuentladung bei Netzeinspeisung gestoppt." };
        var targets = new[] { desired.VictronWatts, desired.E3dcWatts };
        for (var i = 0; i < 2; i++)
        {
            var unit = live.Batteries[i];
            var bound = JointBatteryAllocator.Allocate([unit], targets[i], TimeSpan.FromSeconds(10), desired.ChargeSource == BatteryChargeSource.PV)[0];
            targets[i] = bound.SignedPowerWatts;
        }
        var secondary = live.Batteries[1];
        var eta = (decimal)Math.Sqrt((double)secondary.Configuration.RoundTripEfficiencyPercent / 100d);
        var secondaryAc = secondary.MeasuredPowerWatts * (secondary.MeasuredPowerWatts >= 0 ? 1 / eta : eta);
        var basePhase = Enumerable.Range(0, 3).Select(i => live.PhaseGridWatts[i] -
            (i < limits.VictronPhaseCount ? live.PrimaryAcBatteryWatts / (decimal)limits.VictronPhaseCount : 0) - secondaryAc / 3m).ToArray();
        var baseTotal = basePhase.Sum();
        var total = targets.Sum();
        decimal scale = 1;
        if (total > 0)
        {
            // Requested Victron watts are AC-side; E3/DC conversion uncertainty is covered conservatively.
            for (var phase = 0; phase < 3; phase++)
            {
                var addition = (phase < limits.VictronPhaseCount ? targets[0] / (decimal)limits.VictronPhaseCount : 0) + targets[1] / eta / 3m;
                if (addition > 0) scale = Math.Min(scale, Math.Max(0, limits.PhaseImportWatts[phase] - basePhase[phase] - 150) / addition);
            }
            var availableAc = live.AcOutWatts.Select(load => Math.Max(0, limits.VictronAcInputWatts - load - 100)).Min() * limits.VictronPhaseCount;
            if (targets[0] > availableAc) targets[0] = availableAc;
            var acAddition = targets[0] + targets[1] / eta;
            if (limits.TotalImportWatts > 0 && acAddition > 0) scale = Math.Min(scale, Math.Max(0, limits.TotalImportWatts - baseTotal - 150) / acAddition);
            if (desired.ChargeSource == BatteryChargeSource.PV && acAddition > 0) scale = Math.Min(scale, Math.Max(0, -baseTotal - 100) / acAddition);
        }
        else if (total < 0) scale = Math.Min(scale, Math.Max(0, baseTotal - 100) / -total);
        scale = Math.Clamp(scale, 0, 1);
        return desired with { VictronWatts = (int)(targets[0] * scale), E3dcWatts = (int)(targets[1] * scale) };
    }
}
