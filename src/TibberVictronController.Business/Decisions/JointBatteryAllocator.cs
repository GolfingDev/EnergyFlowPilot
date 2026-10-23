using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>Splits one signed site target without hiding individual reserves or full batteries.</summary>
public static class JointBatteryAllocator
{
    public static IReadOnlyList<JointBatteryTarget> Allocate(IReadOnlyList<JointBatteryUnit> batteries,
        int signedTargetWatts, TimeSpan duration, bool pvCharge = false)
    {
        var hours = (decimal)duration.TotalHours;
        if (hours <= 0) throw new ArgumentOutOfRangeException(nameof(duration), "Die Planungsdauer muss positiv sein.");
        var charge = signedTargetWatts >= 0;
        var caps = batteries.Select(battery =>
        {
            var c = battery.Configuration;
            var eta = (decimal)Math.Sqrt((double)c.RoundTripEfficiencyPercent / 100d);
            var energy = c.TotalCapacityKwh * battery.State.StateOfChargePercent / 100m;
            var boundary = c.TotalCapacityKwh * (charge ? (pvCharge ? 100 : c.PlanningMaximumStateOfChargePercent) : c.PlanningMinimumStateOfChargePercent) / 100m;
            var available = Math.Max(0, charge ? (boundary - energy) / eta : (energy - boundary) * eta);
            var technicalLimit = charge ? c.MaximumChargePowerWatts : c.MaximumDischargePowerWatts;
            var activeLimit = charge ? battery.AvailableChargeWatts : battery.AvailableDischargeWatts;
            return (int)Math.Floor(Math.Min(Math.Min(technicalLimit, Math.Max(0, activeLimit ?? technicalLimit)), available / hours * 1000m));
        }).ToArray();
        var total = caps.Sum();
        var requested = (int)Math.Min(Math.Abs((long)signedTargetWatts), total);
        var allocation = caps.Select(cap => total == 0 ? 0 : (int)((long)requested * cap / total)).ToArray();
        var remaining = requested - allocation.Sum();
        for (var i = 0; remaining > 0; i = (i + 1) % caps.Length)
            if (allocation[i] < caps[i]) { allocation[i]++; remaining--; }

        return batteries.Select((battery, index) =>
        {
            var signed = charge ? allocation[index] : -allocation[index];
            var eta = (decimal)Math.Sqrt((double)battery.Configuration.RoundTripEfficiencyPercent / 100d);
            var delta = signed / 1000m * hours * (charge ? eta : 1 / eta);
            var soc = battery.State.StateOfChargePercent + delta / battery.Configuration.TotalCapacityKwh * 100m;
            return new JointBatteryTarget(battery.Id, signed, battery.State.StateOfChargePercent, soc);
        }).ToArray();
    }
}
