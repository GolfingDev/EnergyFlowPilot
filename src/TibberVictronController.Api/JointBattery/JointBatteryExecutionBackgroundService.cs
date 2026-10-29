using System.Globalization;
using System.Text.Json;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

/// <summary>Refreshes short-lived joint commands. Persisted recovery obligations survive shutdown and crashes.</summary>
public sealed class JointBatteryExecutionBackgroundService(IServiceScopeFactory scopes, JointBatteryExecutionState state,
    VictronSetpointRefreshState ownership, ILogger<JointBatteryExecutionBackgroundService> logger, IJointBatteryWatchdog watchdog) : BackgroundService
{
    // Hard budgets keep the gap between two watchdog pulses (tick + failure handling + 1 s pause)
    // well below the systemd WatchdogSec, so slow I/O cannot get the whole service killed.
    private static readonly TimeSpan CycleBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FailureBudget = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PlanMaximumAge = TimeSpan.FromSeconds(75);

    private DateTimeOffset lastLoggedAt;
    private string? lastLoggedStatus;
    private string? lastLoggedMessage;
    private DateTimeOffset? deviationLoggedFor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    logger.LogWarning("Gemeinsame Ausfuehrung gestoppt. Fehlertyp={ErrorType} Detail={Detail}", error.GetType().Name, Detail(error) ?? "-");
                    // The loop is alive; pulse before the separately budgeted failure handling.
                    await PulseAsync(stoppingToken);
                    try { await HandleFailureAsync(error); }
                    catch (Exception cleanup) { logger.LogWarning("Gemeinsame Ruecknahme bleibt ausstehend. Fehlertyp={ErrorType}", cleanup.GetType().Name); }
                }
                await PulseAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            // Shutdown cancellation must not suppress stop commands. If cleanup fails the persisted marker stays set.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                using var lease = await ownership.AcquirePublicationAsync(cleanup.Token);
                if (ownership.JointControlActive)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await ReleaseAsync(scope.ServiceProvider, cleanup.Token);
                }
            }
            catch (Exception error) { logger.LogWarning("Ruecknahme beim Beenden bleibt ausstehend. Fehlertyp={ErrorType}", error.GetType().Name); }
        }
    }

    /// <summary>
    /// Messages of these types come from our own checks (coordinator, limiter, hardware readback, RSCP adapter) and
    /// contain only fixed German texts, dbus paths and power values. Other types (sockets, HTTP) may carry addresses
    /// or credentials, so only their type name is reported.
    /// </summary>
    private static string? Detail(Exception error) =>
        error is InvalidOperationException or TimeoutException or InvalidDataException or ArgumentException ? error.Message : null;

    private async Task PulseAsync(CancellationToken token)
    {
        try { await watchdog.PulseAsync(token); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning("Watchdog-Impuls konnte nicht gesendet werden. Fehlertyp={ErrorType}", error.GetType().Name);
        }
    }

    public async Task TickAsync(CancellationToken token)
    {
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(token);
        cycle.CancelAfter(CycleBudget);
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var settings = services.GetRequiredService<IControllerSettingStore>();
        var clock = services.GetRequiredService<IUtcClock>();
        async Task<bool> Flag(string key) => bool.TryParse((await settings.GetSettingAsync(key, cycle.Token))?.Value, out var value) && value;

        IDisposable lease;
        try { lease = await ownership.AcquirePublicationAsync(cycle.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // A slow single-battery publication still holds the gate. Skipping one tick is safe: while joint
            // control owns the site, legacy writers return immediately and never hold the gate this long.
            return;
        }

        using (lease)
        {
            var enabled = await Flag(ControllerSettingDefaults.JointBatteryExecutionEnabledKey);
            var pending = await Flag(ControllerSettingDefaults.JointBatteryRecoveryPendingKey);
            var dryRunSetting = (await settings.GetSettingAsync(ControllerSettingDefaults.VictronDryRunKey, cycle.Token))?.Value;
            var dryRun = !bool.TryParse(dryRunSetting, out var dry) || dry;
            if (!enabled || dryRun)
            {
                // A fixed quarter-hour decision belongs to running execution; after re-enabling, a fresh one is taken.
                state.CommittedSlot = null;
                // Switching joint control off (or to dry-run) also ends a manual charge; it must not resume later unnoticed.
                var manualStore = new JointManualChargeStore(settings, clock);
                if (await manualStore.ReadAsync(cycle.Token) is not null)
                    await FinishManualChargeAsync(services, manualStore, "Manuelle Ladung beendet, weil die gemeinsame Steuerung ausgeschaltet oder im Shadow-Modus ist.", cycle.Token);
            }
            if ((pending && !ownership.JointControlActive) || ((!enabled || dryRun) && (pending || ownership.JointControlActive)))
            {
                ownership.JointControlActive = true;
                await ReleaseAsync(services, cycle.Token);
                return;
            }
            if (!enabled || dryRun)
            {
                state.Set(new(enabled ? "Shadow" : "Disabled", enabled ? "Gemeinsame Ausfuehrung im globalen Shadow-Modus; keine Hardwarebefehle." :
                    "Gemeinsame Hardwareausfuehrung deaktiviert.", clock.UtcNow, false));
                return;
            }
            if (state.BlockedUntilUtc is { } until && clock.UtcNow < until)
            {
                if (pending) await ReleaseAsync(services, cycle.Token);
                return;
            }

            // All preconditions are checked before ownership is claimed. A claim creates a recovery obligation
            // whose release needs working hardware access; claiming with known-missing access would lock the
            // site (and the settings needed to repair it) until manual intervention.
            var (command, blocker) = await PrepareAsync(services, settings, clock, cycle.Token);
            if (command is null)
            {
                if (pending || ownership.JointControlActive) throw new InvalidOperationException(blocker);
                state.Set(new("Blocked", blocker!, clock.UtcNow, false));
                await LogAsync(services, cycle.Token);
                return;
            }

            if (!pending) await SaveRecoveryAsync(settings, clock, true, cycle.Token);
            ownership.JointControlActive = true;
            ownership.Clear();
            await services.GetRequiredService<JointBatteryExecutionCoordinator>().StepAsync(command, cycle.Token);
            await LogAsync(services, cycle.Token);
        }
    }

    /// <summary>Checks every start condition without touching hardware and derives the current command.</summary>
    private async Task<(JointBatteryCommand? Command, string? Blocker)> PrepareAsync(IServiceProvider services,
        IControllerSettingStore settings, IUtcClock clock, CancellationToken token)
    {
        async Task<string?> Value(string key) => (await settings.GetSettingAsync(key, token))?.Value;

        if (!watchdog.IsSupervised)
            return (null, "Gemeinsame Hardwareausfuehrung benoetigt den installierten systemd-Watchdog und Ruecknahme-Helfer.");
        if (!bool.TryParse(await Value(ControllerSettingDefaults.E3dcRscpEnabledKey), out var rscpEnabled) || !rscpEnabled)
            return (null, "Lokaler E3/DC-Zugang ist deaktiviert.");
        foreach (var key in new[] { ControllerSettingDefaults.E3dcRscpHostKey, ControllerSettingDefaults.E3dcRscpPortKey,
            ControllerSettingDefaults.E3dcRscpUserKey, ControllerSettingDefaults.E3dcRscpPasswordKey, ControllerSettingDefaults.E3dcRscpKeyKey })
        {
            if (string.IsNullOrWhiteSpace(await Value(key)))
                return (null, "Der lokale E3/DC-Zugang ist nicht vollstaendig konfiguriert.");
        }
        if (!string.Equals((await Value(ControllerSettingDefaults.VictronControlModeKey))?.Trim(), "externalEss", StringComparison.OrdinalIgnoreCase))
            return (null, "Gemeinsame Steuerung benoetigt den eingerichteten External-ESS-Modus.");
        foreach (var key in new[] { ControllerSettingDefaults.JointBatteryPhase1LimitKey, ControllerSettingDefaults.JointBatteryPhase2LimitKey,
            ControllerSettingDefaults.JointBatteryPhase3LimitKey, ControllerSettingDefaults.JointBatteryVictronInputLimitKey })
        {
            if (!decimal.TryParse(await Value(key), NumberStyles.Number, CultureInfo.InvariantCulture, out var limit) || limit <= 0)
                return (null, "Bestaetigte Anschluss- und Phasengrenzen fehlen.");
        }

        // An operator's manual charge replaces the plan; when it ends, this same tick falls back to the plan.
        var manualStore = new JointManualChargeStore(settings, clock);
        if (await manualStore.ReadAsync(token) is { } manual)
        {
            var manualCommand = await PrepareManualChargeAsync(services, manualStore, manual, clock, token);
            if (manualCommand is not null) return (manualCommand, null);
        }

        var runtime = services.GetService<JointBatteryRuntimeState>()?.Current;
        var plan = runtime?.Plan;
        var now = clock.UtcNow;

        // Only the basic action is fixed. A fresh same-action plan updates watts and the two battery shares.
        // Missing/stale plans retain the last targets, still bounded by live limits on every step.
        if (JointBatterySlotCommitment.Active(state.CommittedSlot, now) is { } committed)
        {
            if (JointBatterySlotCommitment.UpdatedTargets(committed, plan, now) is { } updated)
            {
                state.CommittedSlot = updated;
                return (CommandFrom(updated), null);
            }
            if (JointBatterySlotCommitment.NetDemandReplacement(committed, plan, now) is { } replacement)
            {
                state.CommittedSlot = replacement;
                await SavePlanEventAsync(services, "PV-Annahme durch aktuellen Nettobedarf ersetzt; wirtschaftlich geplante Akku-Unterstuetzung freigegeben.",
                    new { committed.TimeSlot, Previous = committed.Targets, replacement.Targets, replacement.Reason }, token);
                return (CommandFrom(replacement), null);
            }
            if (JointBatterySlotCommitment.PlanDeviates(committed, plan) && deviationLoggedFor != committed.TimeSlot.StartsAtUtc)
            {
                deviationLoggedFor = committed.TimeSlot.StartsAtUtc;
                var fresh = plan!.Slots.First(s => s.TimeSlot == committed.TimeSlot);
                await SavePlanEventAsync(services, "Neuer Plan weicht fuer die laufende Viertelstunde ab; die festgeschriebene Grundaktion bleibt bestehen.",
                    new { committed.TimeSlot, Committed = committed.Targets, NewPlan = fresh.Targets, NewReason = fresh.Reason }, token);
            }
            return (CommandFrom(committed), null);
        }

        if (plan is null)
        {
            // The planner runs in the preview worker; surface its failure reason instead of a bare "plan missing".
            var cause = runtime is { Status: "Blocked" } ? " Ursache: " + runtime.Message : " Die Planung laeuft alle 30 Sekunden; bitte kurz warten.";
            return (null, "Ein gueltiger gemeinsamer Plan fehlt." + cause);
        }
        if (clock.UtcNow - plan.CalculatedAtUtc > PlanMaximumAge)
            return (null, "Die gemeinsame Planung ist veraltet.");
        var slot = plan.Slots.SingleOrDefault(s => s.TimeSlot.StartsAtUtc <= clock.UtcNow && s.TimeSlot.EndsAtUtc > clock.UtcNow);
        if (slot is null)
            return (null, "Der aktuelle gemeinsame Planzeitabschnitt fehlt.");
        if (slot.IsPriceEstimated && slot.ChargeSource == BatteryChargeSource.Grid)
            return (null, "Geschaetzte Preise erlauben keine Netzladung.");

        // First execution step fixes the basic action; subsequent same-action plans may update its targets.
        state.CommittedSlot = slot;
        await SavePlanEventAsync(services, "Planentscheidung fuer die Viertelstunde festgeschrieben.",
            new { slot.TimeSlot, slot.Targets, slot.ChargeSource, slot.PricePerKwh, slot.Reason }, token);
        return (CommandFrom(slot), null);
    }

    // Valid until the end of its quarter hour; every step rebuilds and re-limits the command from live data anyway.
    private static JointBatteryCommand CommandFrom(JointBatterySlot slot) =>
        new(slot.Targets.Single(t => t.Id == "victron").SignedPowerWatts, slot.Targets.Single(t => t.Id == "e3dc").SignedPowerWatts,
            slot.ChargeSource, slot.TimeSlot.EndsAtUtc, slot.Reason);

    private static Task SavePlanEventAsync(IServiceProvider services, string message, object details, CancellationToken token) =>
        services.GetRequiredService<IOperationalEventRepository>().SaveEventAsync(new OperationalEvent(Guid.NewGuid(),
            services.GetRequiredService<IUtcClock>().UtcNow, "JointBatteryPlanCommitment", "Information", message,
            JsonSerializer.Serialize(details)), token);

    /// <returns>The manual charge command, or null when the charge has just ended and the plan takes over again.</returns>
    private async Task<JointBatteryCommand?> PrepareManualChargeAsync(IServiceProvider services, JointManualChargeStore store,
        JointManualChargeRequest manual, IUtcClock clock, CancellationToken token)
    {
        var now = clock.UtcNow;
        // Progress normally comes from the coordinator's reading of the previous tick. Before the first step
        // (or after a pause) both batteries are read once; reading writes nothing to the devices.
        var live = state.LastLive;
        if (live is null || now - live.MeasuredAtUtc > TimeSpan.FromSeconds(5))
        {
            live = await services.GetRequiredService<IJointBatteryHardware>().ReadAsync(token);
            state.LastLive = live;
        }
        if (manual.BaselineKwh is null)
        {
            var baseline = JointManualCharge.StoredEnergyKwh(live.Batteries);
            await store.SaveBaselineAsync(baseline, token);
            manual = manual with { BaselineKwh = baseline };
        }

        // Short validity: every tick re-evaluates progress, so a stale manual command must not outlive a stalled loop.
        var step = JointManualCharge.Evaluate(manual, live.Batteries, now, now.AddSeconds(15));
        if (step.Command is not null) return step.Command;
        await FinishManualChargeAsync(services, store, step.Message, token);
        return null;
    }

    private async Task FinishManualChargeAsync(IServiceProvider services, JointManualChargeStore store, string message, CancellationToken token)
    {
        await store.StopAsync(token);
        state.ManualChargeResult = message;
        logger.LogInformation("{Message}", message);
        await services.GetRequiredService<IOperationalEventRepository>().SaveEventAsync(new OperationalEvent(Guid.NewGuid(),
            services.GetRequiredService<IUtcClock>().UtcNow, "JointBatteryManualCharge", "Information", message, "{}"), token);
    }

    private static Task SaveRecoveryAsync(IControllerSettingStore settings, IUtcClock clock, bool pending, CancellationToken token) =>
        settings.SaveSettingAsync(new(ControllerSettingDefaults.JointBatteryRecoveryPendingKey, pending ? "true" : "false",
            ControllerSettingSensitivity.Normal, clock.UtcNow), token);

    private async Task ReleaseAsync(IServiceProvider services, CancellationToken token)
    {
        ownership.Clear();
        var clock = services.GetRequiredService<IUtcClock>();
        if (await services.GetRequiredService<JointBatteryExecutionCoordinator>().ReleaseAsync(token))
        {
            await SaveRecoveryAsync(services.GetRequiredService<IControllerSettingStore>(), clock, false, token);
            ownership.JointControlActive = false;
            services.GetService<DecisionCalculationTrigger>()?.Signal();
        }
        else state.Set(new("Restoring", "Ruecknahme laeuft; Freigabe erst nach neutralen Messwerten beider Akkus.", clock.UtcNow, true));
    }

    private async Task HandleFailureAsync(Exception error)
    {
        using var timeout = new CancellationTokenSource(FailureBudget);
        using var lease = await ownership.AcquirePublicationAsync(timeout.Token);
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var clock = services.GetRequiredService<IUtcClock>();
        state.BlockedUntilUtc = clock.UtcNow.AddSeconds(30);
        var restored = !ownership.JointControlActive;
        try
        {
            if (ownership.JointControlActive)
            {
                await ReleaseAsync(services, timeout.Token);
                restored = !ownership.JointControlActive;
            }
        }
        catch (Exception cleanupError) { logger.LogWarning("Gemeinsame Ruecknahme bleibt ausstehend. Fehlertyp={ErrorType}", cleanupError.GetType().Name); }
        var cause = $" Ursache ({error.GetType().Name}): {Detail(error) ?? "siehe Fehlertyp"}";
        state.Set(new(restored ? "Blocked" : "Restoring", restored ? "Gemeinsame Steuerung nach Fehler gestoppt; erneuter Versuch fruehestens nach 30 Sekunden." + cause :
            "Gemeinsame Ruecknahme noch nicht bestaetigt; Einzelakku-Steuerung bleibt gesperrt. Ist ein Geraet dauerhaft nicht erreichbar, " +
            "gemeinsame Steuerung deaktivieren und die Ruecknahme nach Pruefung vor Ort im Dashboard manuell bestaetigen." + cause,
            clock.UtcNow, ownership.JointControlActive));
        try { await LogAsync(services, timeout.Token, error.GetType().Name); }
        catch (Exception logError) { logger.LogWarning("Gemeinsamer Fehlerstatus konnte nicht gespeichert werden. Fehlertyp={ErrorType}", logError.GetType().Name); }
    }

    private async Task LogAsync(IServiceProvider services, CancellationToken token, string? errorType = null)
    {
        var current = state.Current;
        if (errorType is null && current.Status == lastLoggedStatus)
        {
            // A start blocker can persist for days (e.g. missing watchdog); log it once per message, not every 30 s.
            var unchanged = current.Status == "Blocked"
                ? current.Message == lastLoggedMessage
                : current.UpdatedAtUtc - lastLoggedAt < TimeSpan.FromSeconds(30);
            if (unchanged) return;
        }
        await services.GetRequiredService<IOperationalEventRepository>().SaveEventAsync(new OperationalEvent(Guid.NewGuid(),
            current.UpdatedAtUtc, "JointBatteryExecution", errorType is null ? "Information" : "Error", current.Message,
            JsonSerializer.Serialize(new { Execution = current, ErrorType = errorType })), token);
        // Keep the existing Victron history/accounting tied to executed joint targets, not to a separate old plan.
        if (current.Status == "Active")
        {
            var projection = await services.GetRequiredService<ICurrentBatteryDecisionService>().CalculateCurrentDecisionAsync(token);
            await services.GetRequiredService<IDecisionLogRepository>().SaveDecisionAsync(new(Guid.NewGuid(), projection.DecidedAtUtc,
                projection.ValidFromUtc, projection.ValidToUtc, projection.Decision, projection.BatteryState.StateOfChargePercent,
                projection.TibberPricePerKwh, projection.TibberPriceCurrency, Math.Max(0, projection.SiteTelemetry.CurrentGridImportWatts),
                Math.Max(0, -projection.SiteTelemetry.CurrentGridImportWatts), projection.InputSummaryJson, projection.Reasons), token);
        }
        lastLoggedStatus = current.Status; lastLoggedAt = current.UpdatedAtUtc; lastLoggedMessage = current.Message;
    }
}
