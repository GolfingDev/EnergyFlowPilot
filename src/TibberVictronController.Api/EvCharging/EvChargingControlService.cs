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
    private static readonly TimeSpan LimitValidity = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfterRejection = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TelemetryMaximumAge = TimeSpan.FromMinutes(2);
    // Six samples at 30 s: clouds passing for a minute do not toggle the charging.
    private const int SurplusSamples = 6;

    private readonly Queue<double?> availableSamples = new();
    private EvChargePlan? plan;
    private string? planProblem;
    private IReadOnlyList<string> planMissingSettings = [];
    private DateTimeOffset planCalculatedAtUtc = DateTimeOffset.MinValue;
    private bool limitActive;
    private double? appliedAmps;
    private DateTimeOffset appliedUntilUtc;
    private DateTimeOffset retryAtUtc = DateTimeOffset.MinValue;
    private string? commandProblem;
    private (EvBaseChargeMode Mode, double? Target) planChoice;
    private (EvChargingMode Mode, double? Amps)? loggedDecision;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Automatische Wallbox-Steuerung: Zyklus fehlgeschlagen.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

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
            await ApplyAsync(stop, now, cancellationToken);
            LogChange(stop);
            state.Set(new EvChargingControlSnapshot(enabled, stop.Mode, stop.Amps, stop.Reason, now, commandProblem));
            return;
        }

        if (!enabled)
        {
            if (limitActive) await ReleaseAsync(cancellationToken);
            load.Clear();
            plan = null;
            state.Set(new EvChargingControlSnapshot(false, EvChargingMode.Off, null, "Automatik ausgeschaltet; die Wallbox lädt nach eigener Einstellung.", now, commandProblem));
            return;
        }

        // A new answer or target applies at once, not only with the next quarter hour.
        await RefreshPlanAsync(scope.ServiceProvider, now, (choice.Mode, choice.ImmediateTargetPercent) != planChoice, cancellationToken);
        planChoice = (choice.Mode, choice.ImmediateTargetPercent);
        var wallbox = charger.Current;
        await SampleSurplusAsync(scope.ServiceProvider, wallbox, now, cancellationToken);
        if (!wallbox.IsConnected || !wallbox.IsVehiclePlugged)
        {
            state.Set(new EvChargingControlSnapshot(true, EvChargingMode.Waiting, null,
                wallbox.IsConnected ? "Kein Auto angesteckt." : "Wallbox nicht verbunden.", now, commandProblem));
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
        var decision = EvChargingDecider.Decide(new EvChargingDecisionInput(now, plan, planProblem, AverageAvailable()) { PvGridAllowanceWatts = allowance });
        await ApplyAsync(decision, now, cancellationToken);
        LogChange(decision);
        state.Set(new EvChargingControlSnapshot(true, decision.Mode, decision.Amps, decision.Reason, now, commandProblem) { MissingSettings = planMissingSettings });
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
        catch (Exception exception) when (exception is not OperationCanceledException)
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Missing inputs must not stop charging; the decider then releases the wallbox to full power.
            logger.LogWarning("Ladeplan konnte nicht berechnet werden: {Reason}", exception.Message);
            plan = null;
            planProblem = exception.Message;
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
            if (now - telemetry.MeasuredAtUtc <= TelemetryMaximumAge)
                available = -telemetry.CurrentGridImportWatts + (wallbox.IsConnected ? wallbox.PowerWatts ?? 0 : 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No live surplus this cycle; the plan still applies.
        }

        availableSamples.Enqueue(available);
        while (availableSamples.Count > SurplusSamples) availableSamples.Dequeue();
    }

    // Only a full window of valid samples counts; one missing value means no surplus decision.
    private double? AverageAvailable() =>
        availableSamples.Count == SurplusSamples && availableSamples.All(sample => sample is not null) ? availableSamples.Average() : null;

    private async Task ApplyAsync(EvChargingDecision decision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (decision.Amps is null)
        {
            if (limitActive) await ReleaseAsync(cancellationToken);
            return;
        }

        var unchanged = limitActive && appliedAmps == decision.Amps && appliedUntilUtc - now > RenewBefore;
        if (unchanged || now < retryAtUtc) return;
        try
        {
            var result = await control.LimitCurrentAsync(decision.Amps.Value, LimitValidity, cancellationToken);
            if (result.Accepted)
            {
                (limitActive, appliedAmps, appliedUntilUtc, commandProblem) = (true, decision.Amps, result.ValidUntilUtc ?? now + LimitValidity, null);
                return;
            }

            commandProblem = result.Description;
        }
        catch (InvalidOperationException exception)
        {
            commandProblem = exception.Message;
        }

        // The wallbox refuses: try again later instead of every cycle.
        retryAtUtc = now + RetryAfterRejection;
        logger.LogWarning("Automatische Wallbox-Steuerung: {Problem}", commandProblem);
    }

    private async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await control.ClearLimitAsync(cancellationToken);
            commandProblem = result.Accepted ? null : result.Description;
        }
        catch (InvalidOperationException exception)
        {
            commandProblem = exception.Message;
        }

        // Either way the controller's limit expires by itself within 15 minutes.
        (limitActive, appliedAmps) = (false, null);
    }
}
