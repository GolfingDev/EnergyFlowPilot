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
    // Retain command boundaries until a new SoC sample can confirm their energy. Replanning with
    // the same sample must neither discard earlier charging time nor credit an unmeasured purchase.
    public IReadOnlyList<GridChargeCommandWindow> PendingCommands { get; init; } = [];

    public static GridChargeEnergyCredit Update(DecisionLogEntry? previous, BatteryState battery,
        BatteryConfiguration configuration, string source)
    {
        var credit = 0m;
        var commands = new List<GridChargeCommandWindow>();
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
                        // A later decision supersedes the old command, even when it switches to idle,
                        // manual or PV charging. Preserve only the time each automatic command covered.
                        foreach (var command in old.PendingCommands ?? [])
                        {
                            if (command.StartsAtUtc.Offset != TimeSpan.Zero || command.EndsAtUtc.Offset != TimeSpan.Zero ||
                                command.GridInputWatts < 0m || command.EndsAtUtc <= command.StartsAtUtc)
                                throw new JsonException();
                            var end = command.EndsAtUtc < previous.DecidedAtUtc ? command.EndsAtUtc : previous.DecidedAtUtc;
                            if (end > command.StartsAtUtc && end > old.MeasuredAtUtc)
                                commands.Add(command with { EndsAtUtc = end });
                        }
                        var efficiency = (decimal)Math.Sqrt((double)(configuration.RoundTripEfficiencyPercent / 100m));
                        if (previous.Decision.Instruction.ChargeSource == BatteryChargeSource.Grid &&
                            !previous.Reasons.Any(reason => reason.RuleName == CurrentBatteryDecisionRuleIds.ManualGridCharge) &&
                            json.RootElement.TryGetProperty("CurrentPvProductionWatts", out var pv))
                        {
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
                            var start = previous.ValidFromUtc > previous.DecidedAtUtc ? previous.ValidFromUtc : previous.DecidedAtUtc;
                            if (previous.ValidToUtc > start)
                                commands.Add(new(start, previous.ValidToUtc, Math.Max(0m,
                                    Math.Min(previous.Decision.TargetPowerWatts, configuration.MaximumChargePowerWatts) - surplusWatts)));
                        }
                        if (delta > 0m)
                        {
                            var maximumGain = commands.Sum(command =>
                            {
                                var start = command.StartsAtUtc > old.MeasuredAtUtc ? command.StartsAtUtc : old.MeasuredAtUtc;
                                var end = command.EndsAtUtc < battery.MeasuredAtUtc ? command.EndsAtUtc : battery.MeasuredAtUtc;
                                return Math.Min(command.GridInputWatts, configuration.MaximumChargePowerWatts) / 1000m *
                                    (decimal)Math.Max(0d, (end - start).TotalHours) * efficiency;
                            });
                            credit += Math.Min(delta, maximumGain);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
            {
                // Old/malformed history is not evidence of a paid purchase. Start conservatively with no credit.
                credit = 0m;
                commands.Clear();
            }
        }

        var usable = configuration.TotalCapacityKwh *
            Math.Max(0m, battery.StateOfChargePercent - configuration.PlanningMinimumStateOfChargePercent) / 100m;
        return new GridChargeEnergyCredit(Math.Clamp(credit, 0m, usable), battery.StateOfChargePercent,
            battery.MeasuredAtUtc, configuration.TotalCapacityKwh, source)
        {
            PendingCommands = commands.Where(command => command.EndsAtUtc > battery.MeasuredAtUtc).ToArray()
        };
    }
}

internal sealed record GridChargeCommandWindow(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, decimal GridInputWatts);
