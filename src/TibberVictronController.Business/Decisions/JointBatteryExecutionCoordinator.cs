using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>One coordinator owns both devices. Direction changes require two neutral observations before energizing.</summary>
public sealed class JointBatteryExecutionCoordinator(IJointBatteryHardware hardware, JointBatteryExecutionState state, IUtcClock clock)
{
    public async Task StepAsync(JointBatteryCommand desired, CancellationToken token)
    {
        var live = await hardware.ReadAsync(token);
        state.LastLive = live;
        var command = JointBatteryLiveLimiter.Limit(desired, live, clock.UtcNow);
        var direction = Math.Sign(command.VictronWatts + command.E3dcWatts);
        if (state.Direction != direction)
        { state.NeutralSamples = 0; state.SettlingSinceUtc = clock.UtcNow; }
        if (state.Direction != direction || state.NeutralSamples < 2)
        {
            await NeutralAsync(token);
            if (live.Batteries.All(b => Math.Abs(b.MeasuredPowerWatts) <= 100) && live.E3dcOperatingMode == 0)
                state.NeutralSamples++;
            else state.NeutralSamples = 0;
            state.Direction = direction;
            state.SettlingSinceUtc ??= clock.UtcNow;
            if (clock.UtcNow - state.SettlingSinceUtc > TimeSpan.FromSeconds(30))
                throw new InvalidOperationException("Beide Akkus bestaetigen den neutralen Uebergang nicht.");
            state.Set(new("Transition", "Beide Akkus werden vor der gemeinsamen Aktion neutral geschaltet.", clock.UtcNow, true,
                VictronMeasuredWatts: live.Batteries[0].MeasuredPowerWatts, E3dcMeasuredWatts: live.Batteries[1].MeasuredPowerWatts));
            if (state.NeutralSamples == 2) state.SettlingSinceUtc = clock.UtcNow;
            return;
        }
        var previous = state.Current;
        // Ramp increases only. A budget reduction or stop is immediate, not delayed by the ramp.
        static int Ramp(int old, int target) => Math.Sign(target) * Math.Min(Math.Abs(target), Math.Abs(old) + 250);
        command = command with { VictronWatts = Ramp(previous.VictronTargetWatts, command.VictronWatts), E3dcWatts = Ramp(previous.E3dcTargetWatts, command.E3dcWatts) };
        if ((previous.Status != "Starting" && ((previous.VictronTargetWatts == 0 && command.VictronWatts != 0) || (previous.E3dcTargetWatts == 0 && command.E3dcWatts != 0))) ||
            Math.Abs(previous.VictronTargetWatts - command.VictronWatts) > 500 || Math.Abs(previous.E3dcTargetWatts - command.E3dcWatts) > 500)
            state.SettlingSinceUtc = clock.UtcNow;
        var e3dcMode = command.E3dcWatts == 0 ? E3dcPowerMode.Idle : command.E3dcWatts < 0 ? E3dcPowerMode.Discharge :
            command.ChargeSource == BatteryChargeSource.PV ? E3dcPowerMode.ChargePv : E3dcPowerMode.ChargeGrid;
        var ack = await hardware.SetE3dcAsync(new(e3dcMode, Math.Abs(command.E3dcWatts)), token);
        // EMS_MODE readback uses its own scale (0 idle, 1 discharge, 2 charge), not the SET_POWER mode numbers.
        // Grid and PV charging both report 2.
        var expectedMode = e3dcMode switch
        {
            E3dcPowerMode.Idle => 0,
            E3dcPowerMode.Discharge => 1,
            _ => 2
        };
        if (ack.EchoPowerWatts != Math.Abs(command.E3dcWatts))
            throw new InvalidOperationException("E3/DC bestaetigt den Leistungswert nicht.");
        if (ack.OperatingMode != expectedMode)
        {
            state.SettlingSinceUtc ??= clock.UtcNow;
            if (clock.UtcNow - state.SettlingSinceUtc > TimeSpan.FromSeconds(90))
                throw new InvalidOperationException("E3/DC bestaetigt den angeforderten Betriebsmodus nicht.");
            await hardware.SetVictronAsync(0, token);
            state.Set(new("Starting", "E3/DC-Betriebsmodus wird zurueckgelesen; Victron bleibt bis zur Bestaetigung neutral.",
                clock.UtcNow, true, 0, command.E3dcWatts, live.Batteries[0].MeasuredPowerWatts, ack.MeasuredPowerWatts, command.ValidUntilUtc));
            return;
        }
        await hardware.SetVictronAsync(command.VictronWatts, token);
        var measured = new[] { live.Batteries[0].MeasuredPowerWatts, live.Batteries[1].MeasuredPowerWatts };
        var targets = new[] { command.VictronWatts, command.E3dcWatts };
        if (targets.Zip(measured).Any(p => Math.Sign(p.First) * p.Second < -150))
            throw new InvalidOperationException("Ein Akku arbeitet gegen die gemeinsame Sollrichtung.");
        state.SettlingSinceUtc ??= clock.UtcNow;
        if (clock.UtcNow - state.SettlingSinceUtc > TimeSpan.FromSeconds(90) && targets.Zip(measured).Any(p =>
            Math.Abs(p.First) > 100 && (Math.Sign(p.First) * p.Second < Math.Abs(p.First) * .4 || Math.Abs(p.Second) > Math.Abs(p.First) + 500)))
            throw new InvalidOperationException("Die gemessenen Leistungen folgen den gemeinsamen Sollwerten nicht.");
        state.Set(new("Active", command.Reason, clock.UtcNow, true, command.VictronWatts, command.E3dcWatts,
            measured[0], measured[1], command.ValidUntilUtc));
    }

    private async Task NeutralAsync(CancellationToken token)
    {
        await hardware.SetVictronAsync(0, token);
        var ack = await hardware.SetE3dcAsync(new(E3dcPowerMode.Idle, 0), token);
        if (ack.EchoPowerWatts != 0)
            throw new InvalidOperationException("E3/DC bestaetigt die Entlade- und Ladesperre nicht.");
    }

    public async Task<bool> ReleaseAsync(CancellationToken token)
    {
        // The Victron stops first; E3/DC may resume autonomous discharge only after both have settled.
        await NeutralAsync(token);
        var live = await hardware.ReadAsync(token);
        if (live.Batteries.Any(b => Math.Abs(b.MeasuredPowerWatts) > 100)) return false;
        var ack = await hardware.SetE3dcAsync(new(E3dcPowerMode.Automatic, 0), token);
        if (ack.EchoPowerWatts != 0) return false;
        state.Direction = 0; state.NeutralSamples = 0; state.SettlingSinceUtc = null;
        state.Set(new("Disabled", "Gemeinsame Sollwerte beendet; vorherige automatische Regelung wieder freigegeben.", clock.UtcNow, false));
        return true;
    }
}
