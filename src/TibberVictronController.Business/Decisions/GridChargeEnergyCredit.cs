using System.Text.Json;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Carries the already-paid planning hurdle across cycles using measured, not requested, energy.
/// Stored in the existing decision log so a short process restart does not erase it.
/// </summary>
internal sealed record GridChargeEnergyCredit(
    decimal EnergyKwh, decimal StateOfChargePercent, DateTimeOffset MeasuredAtUtc,
    decimal CapacityKwh, string Source)
{
    public static GridChargeEnergyCredit Update(DecisionLogEntry? previous, BatteryState battery,
        BatteryConfiguration configuration, string source)
    {
        var credit = 0m;
        if (previous is not null)
        {
            try
            {
                using var json = JsonDocument.Parse(previous.InputSummaryJson);
                if (json.RootElement.TryGetProperty(nameof(GridChargeEnergyCredit), out var element))
                {
                    var old = element.Deserialize<GridChargeEnergyCredit>();
                    var elapsed = old is null ? TimeSpan.Zero : battery.MeasuredAtUtc - old.MeasuredAtUtc;
                    if (old is not null && old.EnergyKwh >= 0m && old.StateOfChargePercent is >= 0m and <= 100m &&
                        old.MeasuredAtUtc.Offset == TimeSpan.Zero && old.Source == source && old.CapacityKwh == configuration.TotalCapacityKwh &&
                        elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromMinutes(30))
                    {
                        var delta = (battery.StateOfChargePercent - old.StateOfChargePercent) * old.CapacityKwh / 100m;
                        credit = Math.Max(0m, old.EnergyKwh + Math.Min(0m, delta));
                        // Reject unobserved gaps and PV-only gains. The previous command must still cover the sample.
                        if (delta > 0m && battery.MeasuredAtUtc <= previous.ValidToUtc &&
                            previous.Decision.Instruction.ChargeSource == BatteryChargeSource.Grid &&
                            !previous.Reasons.Any(reason => reason.RuleName == CurrentBatteryDecisionRuleIds.ManualGridCharge) &&
                            json.RootElement.TryGetProperty("CurrentPvProductionWatts", out var pv))
                        {
                            var efficiency = (decimal)Math.Sqrt((double)(configuration.RoundTripEfficiencyPercent / 100m));
                            // Deduct PV surplus, not all PV production: PV serving house demand is not battery input.
                            var surplusWatts = Math.Max(0m, pv.GetInt32());
                            if (json.RootElement.TryGetProperty("CurrentGridImportWatts", out var grid) &&
                                json.RootElement.TryGetProperty("CurrentBatteryPowerWatts", out var power) &&
                                power.ValueKind == JsonValueKind.Number)
                            {
                                var dcWatts = power.GetDecimal();
                                var acWatts = dcWatts >= 0m ? dcWatts / efficiency : dcWatts * efficiency;
                                surplusWatts = Math.Max(0m, -(grid.GetDecimal() - acWatts));
                            }
                            var commandHours = (decimal)Math.Max(0d,
                                (battery.MeasuredAtUtc - (previous.DecidedAtUtc > old.MeasuredAtUtc
                                    ? previous.DecidedAtUtc : old.MeasuredAtUtc)).TotalHours);
                            var maximumGain = Math.Max(0m,
                                Math.Min(previous.Decision.TargetPowerWatts, configuration.MaximumChargePowerWatts) - surplusWatts)
                                / 1000m * commandHours * efficiency;
                            credit += Math.Min(delta, maximumGain);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
            {
                // Old/malformed history is not evidence of a paid purchase. Start conservatively with no credit.
                credit = 0m;
            }
        }

        var usable = configuration.TotalCapacityKwh *
            Math.Max(0m, battery.StateOfChargePercent - configuration.PlanningMinimumStateOfChargePercent) / 100m;
        return new GridChargeEnergyCredit(Math.Clamp(credit, 0m, usable), battery.StateOfChargePercent,
            battery.MeasuredAtUtc, configuration.TotalCapacityKwh, source);
    }
}
