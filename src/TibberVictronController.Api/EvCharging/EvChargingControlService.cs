using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.EvCharging;

public sealed record EvChargingControlSnapshot(
    bool Enabled,
    EvChargingMode Mode,
    double? Amps,
    string Reason,
    DateTimeOffset UpdatedAtUtc,
    string? CommandProblem)
{
    /// <summary>Settings the plan is missing; the car page offers to enter them.</summary>
    public IReadOnlyList<string> MissingSettings { get; init; } = [];
}

public sealed class EvChargingControlState
{
    private EvChargingControlSnapshot current = new(false, EvChargingMode.Off, null, "Automatik ausgeschaltet.", DateTimeOffset.MinValue, null);

    public EvChargingControlSnapshot Current => Volatile.Read(ref current);

    public void Set(EvChargingControlSnapshot snapshot) => Volatile.Write(ref current, snapshot);
}

/// <summary>
/// Planned car charging energy per quarter hour, for the joint battery plan (stage 4d). Filled only while the
/// automatic control is on, because only then does the car actually follow the plan.
/// </summary>
public sealed class EvChargeLoadForecast
{
    private IReadOnlyDictionary<DateTimeOffset, double> kwhBySlotStart = new Dictionary<DateTimeOffset, double>();

    public double KwhFor(DateTimeOffset slotStartUtc) => Volatile.Read(ref kwhBySlotStart).GetValueOrDefault(slotStartUtc);

    public void Set(EvChargePlan plan) => Volatile.Write(ref kwhBySlotStart,
        plan.Slots.ToDictionary(slot => slot.StartsAtUtc, slot => slot.GridKwh + slot.PvKwh));

    public void Clear() => Volatile.Write(ref kwhBySlotStart, new Dictionary<DateTimeOffset, double>());

    /// <summary>Start of the running or next quarter hour with planned car energy; null without a plan.</summary>
    public DateTimeOffset? NextPlannedStartUtc(DateTimeOffset nowUtc) => Volatile.Read(ref kwhBySlotStart)
        .Where(slot => slot.Value > 0 && slot.Key.AddMinutes(15) > nowUtc)
        .Select(slot => (DateTimeOffset?)slot.Key)
        .Min();
}

/// <summary>
/// Automatic wallbox control (stage 4c). Every 30 seconds: follow the planned quarter hour or live PV surplus, else
/// pause; without a plan, no limit at all. Limits are sent only on change and renewed before they expire; because
/// every limit expires after 15 minutes, a stopped controller leaves the wallbox charging at full power.
/// The rules live in Business (<see cref="EvChargingDecider"/>, <see cref="EvSurplusChargingTracker"/>,
/// <see cref="EvChargerLimitKeeper"/>); this service only collects the inputs and runs the cycle.
/// </summary>
public sealed class EvChargingControlService(
    IServiceScopeFactory scopes,
    IEvChargerStatusSource charger,
    IEvChargerControl control,
    EvChargingControlState state,
    EvChargeLoadForecast load,
    IUtcClock clock,
    ILogger<EvChargingControlService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PlanRefresh = TimeSpan.FromMinutes(5);

    // Surplus readings of the last three minutes, and since when surplus charging runs or pauses.
    private readonly EvSurplusChargingTracker surplus = new();
    private readonly EvChargerLimitKeeper limits = new(control, clock);
    private EvChargePlan? plan;
    private string? planProblem;
    private IReadOnlyList<string> planMissingSettings = [];
    private DateTimeOffset planCalculatedAtUtc = DateTimeOffset.MinValue;
    private string? loggedProblem;
    private (EvBaseChargeMode Mode, double? Target) planChoice;
    private (EvChargingMode Mode, double? Amps)? loggedDecision;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await RunCycleAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One control cycle that never fails: only the host stopping ends the loop. Every other error is logged and the
    /// next cycle tries again. This includes cancellations that are not ours (a wallbox dropping its connection during
    /// a command, an HTTP timeout while the plan is calculated): an exception leaving the loop would stop the whole
    /// host, and with it the battery control.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await TickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Automatische Wallbox-Steuerung: Zyklus fehlgeschlagen.");
        }
    }

    // Cancellation counts as "stop" only when it is our own token; see RunCycleAsync.
    private static bool IsFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var scope = scopes.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<IControllerSettingStore>();
        var enabled = bool.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.EvChargingControlEnabledKey, cancellationToken))?.Value, out var on) && on;
        var choice = await scope.ServiceProvider.GetRequiredService<EvBaseChargeChoiceService>().GetAsync(cancellationToken);
        // Stopped by hand: 0 A for this charging session, with or without the automatic control (operator 2026-11-03).
        if (choice.IsStopped && charger.Current is { IsConnected: true, IsVehiclePlugged: true })
        {
            var stop = new EvChargingDecision(EvChargingMode.Paused, 0,
                $"Laden von Hand gestoppt{(choice.StoppedBy is null ? string.Empty : $" ({choice.StoppedBy})")}; weiter mit \u201eFortsetzen\u201c oder neu anstecken.");
            // A stop by hand is immediate; minimum run time and pause of the surplus rules do not apply to it.
            surplus.Reset();
            await ApplyAsync(stop.Amps, cancellationToken);
            LogChange(stop);
            state.Set(new EvChargingControlSnapshot(enabled, stop.Mode, stop.Amps, stop.Reason, now, limits.CommandProblem));
            return;
        }

        if (!enabled)
        {
            surplus.Reset();
            await ApplyAsync(null, cancellationToken);
            load.Clear();
            plan = null;
            state.Set(new EvChargingControlSnapshot(false, EvChargingMode.Off, null, "Automatik ausgeschaltet; die Wallbox lädt nach eigener Einstellung.", now, limits.CommandProblem));
            return;
        }

        // A new answer or target applies at once, not only with the next quarter hour.
        await RefreshPlanAsync(scope.ServiceProvider, now, (choice.Mode, choice.ImmediateTargetPercent) != planChoice, cancellationToken);
        planChoice = (choice.Mode, choice.ImmediateTargetPercent);
        var wallbox = charger.Current;
        await SampleSurplusAsync(scope.ServiceProvider, wallbox, now, cancellationToken);
        if (!wallbox.IsConnected || !wallbox.IsVehiclePlugged)
        {
            // Unplugged: the next car starts without run time or pause. A wallbox that merely lost its connection
            // keeps charging at its last limit, so the surplus state stays as it is.
            if (wallbox.IsConnected) surplus.Reset();
            state.Set(new EvChargingControlSnapshot(true, EvChargingMode.Waiting, null,
                wallbox.IsConnected ? "Kein Auto angesteckt." : "Wallbox nicht verbunden.", now, limits.CommandProblem));
            return;
        }

        if (choice is { ShouldAsk: true, TransactionId: { } transaction })
        {
            // Once per charging session; the page (and later a push message) shows the question until it is answered.
            await scope.ServiceProvider.GetRequiredService<IEvChargingSessionRepository>().MarkBaseChoiceAskedAsync(transaction, now, cancellationToken);
            logger.LogInformation("Auto bei {Soc:0} % unter dem Grund-Ladestand von {Base:0} %: Frage \"jetzt oder preisgesteuert\" gestellt.",
                choice.SocPercent, choice.BaseSocPercent);
            await AskByPushAsync(scope.ServiceProvider, choice, transaction, now, cancellationToken);
        }

        var allowance = double.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.EvChargingPvGridAllowanceKey, cancellationToken))?.Value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var watts) ? watts : 0;
        var decision = EvChargingDecider.Decide(new EvChargingDecisionInput(now, plan, planProblem, surplus.AverageAvailableWatts(now))
        {
            PvGridAllowanceWatts = allowance,
            Surplus = surplus.State
        });
        surplus.Observe(decision, now);
        await ApplyAsync(decision.Amps, cancellationToken);
        LogChange(decision);
        state.Set(new EvChargingControlSnapshot(true, decision.Mode, decision.Amps, decision.Reason, now, limits.CommandProblem) { MissingSettings = planMissingSettings });
    }

    // Each change of the wallbox decision is logged with its reason, so a charging night can be explained afterwards
    // (operator 2026-11-06: charged to 60 % instead of 50 %, and the log did not say why).
    private void LogChange(EvChargingDecision decision)
    {
        if (loggedDecision == (decision.Mode, decision.Amps)) return;
        loggedDecision = (decision.Mode, decision.Amps);
        logger.LogInformation("Wallbox-Steuerung: {Reason}", decision.Reason);
    }

    // Every driver device with notifications gets the question; the first answer counts. A failed push only costs the
    // question: the page still shows it, and without an answer the plan charges by price.
    private async Task AskByPushAsync(IServiceProvider services, EvBaseChargeChoiceState choice, int transaction, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
            var defaultAt = TimeZoneInfo.ConvertTime(now + EvBaseChargeChoice.AnswerTime, zone);
            var tokens = services.GetRequiredService<Push.BaseChoiceAnswerToken>();
            var message = new Push.PushMessage("base-choice", "Auto unter dem Grund-Ladestand",
                $"Ladestand {choice.SocPercent:0} %, Grund-Ladestand {choice.BaseSocPercent:0} %. Jetzt laden oder preisgesteuert? Ohne Antwort ab {defaultAt:HH:mm} Uhr preisgesteuert.",
                $"ev-base-choice-{transaction}", "/car")
            {
                Actions = [new Push.PushAction("Immediate", "Jetzt laden"), new Push.PushAction("PriceDriven", "Preisgesteuert"), new Push.PushAction("Stop", "Nicht laden")]
            };
            var (delivered, failed) = await services.GetRequiredService<Push.WebPushService>().SendAsync(
                subscription => message with { Token = tokens.Create(transaction, subscription.UserName) }, null, EvBaseChargeChoice.AnswerTime, cancellationToken);
            if (delivered + failed == 0)
                logger.LogWarning("Frage zum Grund-Ladestand nicht per Push gesendet: kein Gerät mit eingeschalteten Benachrichtigungen angemeldet.");
            else
                logger.LogInformation("Frage zum Grund-Ladestand per Push: {Delivered} zugestellt, {Failed} fehlgeschlagen.", delivered, failed);
        }
        catch (Exception exception) when (IsFailure(exception, cancellationToken))
        {
            logger.LogWarning(exception, "Frage zum Grund-Ladestand konnte nicht per Push gesendet werden.");
        }
    }

    private async Task RefreshPlanAsync(IServiceProvider services, DateTimeOffset now, bool force, CancellationToken cancellationToken)
    {
        var quarterChanged = now.Ticks / TimeSpan.FromMinutes(15).Ticks != planCalculatedAtUtc.Ticks / TimeSpan.FromMinutes(15).Ticks;
        if (plan is not null && !force && !quarterChanged && now - planCalculatedAtUtc < PlanRefresh) return;
        try
        {
            plan = (await services.GetRequiredService<EvChargePlanService>().GetAsync(cancellationToken)).Plan;
            planProblem = null;
            planMissingSettings = [];
            load.Set(plan);
        }
        catch (Exception exception) when (IsFailure(exception, cancellationToken))
        {
            // Missing inputs must not stop charging; the decider then releases the wallbox to full power. A timeout of
            // a price or forecast request arrives as a cancellation and is such a missing input too.
            var reason = exception is OperationCanceledException ? "Zeitüberschreitung bei der Berechnung des Ladeplans." : exception.Message;
            logger.LogWarning("Ladeplan konnte nicht berechnet werden: {Reason}", reason);
            plan = null;
            planProblem = reason;
            planMissingSettings = MissingSettingException.KeysOf(exception);
            load.Clear();
        }

        planCalculatedAtUtc = now;
    }

    private async Task SampleSurplusAsync(IServiceProvider services, EvChargerSnapshot wallbox, DateTimeOffset now, CancellationToken cancellationToken)
    {
        double? available = null;
        try
        {
            var telemetry = await services.GetRequiredService<ICurrentSiteTelemetryProvider>().GetCurrentSiteTelemetryAsync(cancellationToken);
            available = EvSurplusSample.AvailableWatts(telemetry, wallbox.IsConnected ? wallbox.PowerWatts ?? 0 : 0, now);
        }
        catch (Exception exception) when (IsFailure(exception, cancellationToken))
        {
            // No live surplus this cycle; the plan still applies.
        }

        surplus.AddSample(now, available);
    }

    // Null releases the limit. The keeper never throws for a failed command; it reports the problem instead.
    private async Task ApplyAsync(double? amps, CancellationToken cancellationToken)
    {
        await limits.ApplyAsync(amps, cancellationToken);
        // A repeated command (every cycle after a lost answer) must not repeat its warning every 30 seconds.
        if (limits.CommandProblem is { } problem && problem != loggedProblem)
            logger.LogWarning("Automatische Wallbox-Steuerung: {Problem}", problem);
        loggedProblem = limits.CommandProblem;
    }
}
