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
        var e3dcMode = command.E3dcWatts == 0 ? E3dcPowerMode.Idle : command.E3dcWatts < 0 ? E3dcPowerMode.Discharge :
            command.ChargeSource == BatteryChargeSource.PV ? E3dcPowerMode.ChargePv : E3dcPowerMode.ChargeGrid;
        var requestedE3dc = Math.Abs(command.E3dcWatts);
        var ack = await hardware.SetE3dcAsync(new(e3dcMode, requestedE3dc), token);

        // The E3/DC answers with the power it actually grants. Less than requested is its own limit (e.g. the 97 W it
        // reports at the reserve), not a rejection: continue with the granted power and report it. More than requested
        // is never plausible. All site limits were computed for the full request, so a smaller grant is always safe.
        if (ack.EchoPowerWatts < 0 || ack.EchoPowerWatts > requestedE3dc)
            throw new InvalidOperationException($"E3/DC bestaetigt einen unplausiblen Leistungswert ({ack.EchoPowerWatts} W bei {requestedE3dc} W angefordert).");
        var grantedE3dc = Math.Sign(command.E3dcWatts) * ack.EchoPowerWatts;
        var grantNote = ack.EchoPowerWatts < requestedE3dc ? $" E3/DC gibt nur {ack.EchoPowerWatts} von {requestedE3dc} W frei." : "";

        // Settling restarts when a battery starts or changes notably; measured against what the E3/DC really granted.
        if ((previous.Status != "Starting" && ((previous.VictronTargetWatts == 0 && command.VictronWatts != 0) || (previous.E3dcTargetWatts == 0 && grantedE3dc != 0))) ||
            Math.Abs(previous.VictronTargetWatts - command.VictronWatts) > 500 || Math.Abs(previous.E3dcTargetWatts - grantedE3dc) > 500)
            state.SettlingSinceUtc = clock.UtcNow;

        // EMS_MODE readback uses its own scale (0 idle, 1 discharge, 2 charge), not the SET_POWER mode numbers.
        // Grid and PV charging both report 2. A request granted with 0 W has no defined mode to wait for.
        var expectedMode = e3dcMode switch
        {
            E3dcPowerMode.Idle => 0,
            E3dcPowerMode.Discharge => 1,
            _ => 2
        };
        if ((requestedE3dc == 0 || grantedE3dc != 0) && ack.OperatingMode != expectedMode)
        {
            state.SettlingSinceUtc ??= clock.UtcNow;
            if (clock.UtcNow - state.SettlingSinceUtc > TimeSpan.FromSeconds(90))
                throw new InvalidOperationException($"E3/DC bestaetigt den angeforderten Betriebsmodus nicht (erwartet {expectedMode}, gemeldet {ack.OperatingMode}).");
            await hardware.SetVictronAsync(0, token);
            state.Set(new("Starting", "E3/DC-Betriebsmodus wird zurueckgelesen; Victron bleibt bis zur Bestaetigung neutral." + grantNote,
                clock.UtcNow, true, 0, grantedE3dc, live.Batteries[0].MeasuredPowerWatts, ack.MeasuredPowerWatts, command.ValidUntilUtc));
            return;
        }
        await hardware.SetVictronAsync(command.VictronWatts, token);
        var measured = new[] { live.Batteries[0].MeasuredPowerWatts, live.Batteries[1].MeasuredPowerWatts };
        var targets = new[] { command.VictronWatts, grantedE3dc };
        // The measurement was taken before this step's setpoints, so it still reflects the previous target.
        var previousTargets = new[] { previous.VictronTargetWatts, previous.E3dcTargetWatts };
        string[] names = ["Victron", "E3/DC"];
        for (var i = 0; i < 2; i++)
            if (Math.Sign(targets[i]) * measured[i] < -150)
                throw new InvalidOperationException($"{names[i]} arbeitet gegen die gemeinsame Sollrichtung: Soll {targets[i]} W, gemessen {measured[i]} W.");

        // After settling, only an overshoot is unsafe (it can exceed the phase and feed-line limits the command was
        // sized for). Delivering less than the target is harmless - the battery just charges or discharges less - and
        // happens e.g. when the E3/DC confirms a power but limits it internally. It is reported, not treated as a fault.
        state.SettlingSinceUtc ??= clock.UtcNow;
        var followNote = "";
        if (clock.UtcNow - state.SettlingSinceUtc > TimeSpan.FromSeconds(90))
        {
            for (var i = 0; i < 2; i++)
            {
                if (Math.Abs(targets[i]) <= 100) continue;
                // Overshoot is judged against the larger of the previous and the new target: a target that was just
                // lowered is not yet visible in a measurement taken before the step (false alarms at "Soll -3729 W,
                // gemessen -4318 W", 29.10.2026). Targets are AC-side, measurements DC-side; discharging draws about
                // 10-15 % more from the battery than it delivers, hence the relative allowance on top of the 500 W.
                var sameDirectionPrevious = Math.Sign(previousTargets[i]) == Math.Sign(targets[i]) ? Math.Abs(previousTargets[i]) : 0;
                var reference = Math.Max(Math.Abs(targets[i]), sameDirectionPrevious);
                if (Math.Abs(measured[i]) > reference * 1.2 + 500)
                    throw new InvalidOperationException($"{names[i]} ueberschreitet den Sollwert deutlich: Soll {targets[i]} W, zuvor {previousTargets[i]} W, gemessen {measured[i]} W.");
                if (Math.Sign(targets[i]) * measured[i] < Math.Abs(targets[i]) * .4)
                    followNote += $" {names[i]} folgt nur teilweise: Soll {targets[i]} W, gemessen {measured[i]} W.";
            }
        }
        state.Set(new("Active", command.Reason + grantNote + followNote, clock.UtcNow, true, command.VictronWatts, grantedE3dc,
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
