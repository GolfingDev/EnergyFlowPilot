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
    // 6 s: one E3/DC exchange plus a Victron read that may wait 2.5 s and a setpoint readback of up to 2 s.
    private static readonly TimeSpan CycleBudget = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FailureBudget = TimeSpan.FromSeconds(6);

    private DateTimeOffset lastLoggedAt;
    private string? lastLoggedStatus;
    private string? lastLoggedMessage;
    private DateTimeOffset? deviationLoggedFor;
    private string? fallbackLoggedCause;

    // A persistent fault fails every second (e.g. an unconfirmed release). One event row per iteration was on the order
    // of a thousand rows per hour on the SD card; the stop behaviour itself is not throttled, only its event rows.
    private readonly RepeatedEventThrottle repeatedEvents = new(TimeSpan.FromMinutes(5));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
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

    /// <summary>One loop iteration without the pause: the tick and, when it fails, the stop of both batteries.</summary>
    public async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try { await TickAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning("Gemeinsame Ausfuehrung gestoppt. Fehlertyp={ErrorType} Detail={Detail}", error.GetType().Name, Detail(error) ?? "-");
            // The loop is alive; pulse before the separately budgeted failure handling.
            await PulseAsync(stoppingToken);
            try { await HandleFailureAsync(error); }
            catch (Exception cleanup) { logger.LogWarning("Gemeinsame Ruecknahme bleibt ausstehend. Fehlertyp={ErrorType}", cleanup.GetType().Name); }
        }
    }

    public async Task TickAsync(CancellationToken token)
    {
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(token);
        cycle.CancelAfter(CycleBudget);
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        // One read per setting and tick (device switches excepted), see CachingControllerSettingStore.
        services.GetService<ScopedSettingReadCache>()?.Enable();
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
            var (command, selectedAt, blocker, missingSettings) = await PrepareAsync(services, settings, clock, cycle.Token);
            if (command is null)
            {
                if (pending || ownership.JointControlActive) throw new InvalidOperationException(blocker);
                state.Set(new("Blocked", blocker!, clock.UtcNow, false) { MissingSettings = missingSettings });
                await LogAsync(services, cycle.Token);
                return;
            }

            if (!pending) await SaveRecoveryAsync(settings, clock, true, cycle.Token);
            ownership.JointControlActive = true;
            ownership.Clear();
            // The command is judged against the instant it was selected: a quarter hour ending while the devices are
            // being read is no fault. Measurements are still judged against the current time.
            var outcome = await services.GetRequiredService<JointBatteryExecutionCoordinator>().StepAsync(command, selectedAt, cycle.Token);
            if (outcome == JointBatteryStepOutcome.ReadMissTolerated)
            {
                await ReportToleratedReadMissAsync(services, clock, cycle.Token);
                return;
            }
            await LogAsync(services, cycle.Token);
        }
    }

    /// <summary>Nothing was written and the execution status is unchanged; the skipped tick is only made visible.</summary>
    private async Task ReportToleratedReadMissAsync(IServiceProvider services, IUtcClock clock, CancellationToken token)
    {
        var detail = state.LastReadMissDetail ?? "-";
        logger.LogWarning("Messwerte fuer einen Takt nicht rechtzeitig erhalten; bestaetigte Sollwerte bleiben bestehen. Detail={Detail}", detail);
        if (!repeatedEvents.ShouldWrite("ReadMissTolerated", clock.UtcNow, out var suppressed)) return;
        try
        {
            await services.GetRequiredService<IOperationalEventRepository>().SaveEventAsync(new OperationalEvent(Guid.NewGuid(), clock.UtcNow,
                "JointBatteryExecution", "Warning",
                "Messwerte fuer einen Takt nicht rechtzeitig erhalten; die zuletzt bestaetigten Sollwerte bleiben fuer diesen Takt bestehen." +
                RepeatNote(suppressed), JsonSerializer.Serialize(new { Detail = detail, SuppressedRepeats = suppressed })), token);
        }
        // The note is optional. Failing the tick over it would stop both batteries although nothing is wrong with them.
        catch (Exception error)
        {
            logger.LogWarning("Hinweis auf ausgelassenen Takt konnte nicht gespeichert werden. Fehlertyp={ErrorType}", error.GetType().Name);
        }
    }

    private static string RepeatNote(int suppressed) => suppressed == 0 ? "" :
        $" ({suppressed} gleichartige Meldungen seit der letzten wurden nicht einzeln protokolliert.)";

    /// <summary>Checks every start condition without touching hardware and derives the current command.</summary>
    /// <returns>With a command also the instant it was selected at; the command's validity refers to that instant.</returns>
    private async Task<(JointBatteryCommand? Command, DateTimeOffset SelectedAtUtc, string? Blocker, IReadOnlyList<string> MissingSettings)> PrepareAsync(
        IServiceProvider services, IControllerSettingStore settings, IUtcClock clock, CancellationToken token)
    {
        async Task<string?> Value(string key) => (await settings.GetSettingAsync(key, token))?.Value;

        if (!watchdog.IsSupervised)
            return (null, default, "Gemeinsame Hardwareausfuehrung benoetigt den installierten systemd-Watchdog und Ruecknahme-Helfer.", []);
        if (!bool.TryParse(await Value(ControllerSettingDefaults.E3dcRscpEnabledKey), out var rscpEnabled) || !rscpEnabled)
            return (null, default, "Lokaler E3/DC-Zugang ist deaktiviert.", [ControllerSettingDefaults.E3dcRscpEnabledKey]);
        var missingAccess = new List<string>();
        foreach (var key in new[] { ControllerSettingDefaults.E3dcRscpHostKey, ControllerSettingDefaults.E3dcRscpPortKey,
            ControllerSettingDefaults.E3dcRscpUserKey, ControllerSettingDefaults.E3dcRscpPasswordKey, ControllerSettingDefaults.E3dcRscpKeyKey })
        {
            if (string.IsNullOrWhiteSpace(await Value(key))) missingAccess.Add(key);
        }
        if (missingAccess.Count > 0) return (null, default, "Der lokale E3/DC-Zugang ist nicht vollstaendig konfiguriert.", missingAccess);
        if (!string.Equals((await Value(ControllerSettingDefaults.VictronControlModeKey))?.Trim(), "externalEss", StringComparison.OrdinalIgnoreCase))
            return (null, default, "Gemeinsame Steuerung benoetigt den eingerichteten External-ESS-Modus.", [ControllerSettingDefaults.VictronControlModeKey]);
        var missingLimits = new List<string>();
        foreach (var key in new[] { ControllerSettingDefaults.JointBatteryPhase1LimitKey, ControllerSettingDefaults.JointBatteryPhase2LimitKey,
            ControllerSettingDefaults.JointBatteryPhase3LimitKey, ControllerSettingDefaults.JointBatteryVictronInputLimitKey })
        {
            if (!decimal.TryParse(await Value(key), NumberStyles.Number, CultureInfo.InvariantCulture, out var limit) || limit <= 0) missingLimits.Add(key);
        }
        if (missingLimits.Count > 0) return (null, default, "Bestaetigte Anschluss- und Phasengrenzen fehlen.", missingLimits);

        // An operator's manual charge replaces the plan; when it ends, this same tick falls back to the plan.
        var manualStore = new JointManualChargeStore(settings, clock);
        if (await manualStore.ReadAsync(token) is { } manual)
        {
            // Taken before the manual step, which may read the devices first: earlier is the stricter side for the
            // maximum selection age, and the manual command is valid for 15 s from its own later timestamp.
            var manualSelectedAt = clock.UtcNow;
            var manualCommand = await PrepareManualChargeAsync(services, manualStore, manual, clock, token);
            if (manualCommand is not null) return (manualCommand, manualSelectedAt, null, []);
        }

        var runtime = services.GetService<JointBatteryRuntimeState>()?.Current;
        // One timestamp for selecting the command and for judging its validity later in this tick.
        var selectedAt = clock.UtcNow;
        var selection = JointBatteryTickSelector.Select(new(selectedAt, state.CommittedSlot, runtime?.Plan,
            runtime is { Status: "Blocked" } ? runtime.Message : null));
        state.CommittedSlot = selection.CommittedSlot;
        switch (selection.Outcome)
        {
            case JointBatteryTickOutcome.NetDemandReplacement:
                var previous = selection.PreviousCommittedSlot!;
                await SavePlanEventAsync(services, "PV-Annahme durch aktuellen Nettobedarf ersetzt; wirtschaftlich geplante Akku-Unterstuetzung freigegeben.",
                    new { previous.TimeSlot, Previous = previous.Targets, selection.CommittedSlot!.Targets, selection.CommittedSlot.Reason }, token);
                break;
            case JointBatteryTickOutcome.CommittedDespiteDeviation when deviationLoggedFor != selection.CommittedSlot!.TimeSlot.StartsAtUtc:
                // Once per quarter hour: a plan recalculated every 30 s would otherwise repeat the same note.
                var committed = selection.CommittedSlot;
                var fresh = selection.DeviatingSlot!;
                deviationLoggedFor = committed.TimeSlot.StartsAtUtc;
                await SavePlanEventAsync(services, "Neuer Plan weicht fuer die laufende Viertelstunde ab; die festgeschriebene Grundaktion bleibt bestehen.",
                    new { committed.TimeSlot, Committed = committed.Targets, NewPlan = fresh.Targets, NewReason = fresh.Reason }, token);
                break;
            case JointBatteryTickOutcome.SelfConsumptionFallback when fallbackLoggedCause != selection.FallbackCause:
                fallbackLoggedCause = selection.FallbackCause;
                await services.GetRequiredService<IOperationalEventRepository>().SaveEventAsync(new OperationalEvent(Guid.NewGuid(), clock.UtcNow,
                    "JointBatteryFallback", "Warning", "Notbetrieb Eigenverbrauch ohne gueltigen Plan.", JsonSerializer.Serialize(new { Cause = selection.FallbackCause })), token);
                break;
            case JointBatteryTickOutcome.NewCommitment:
                fallbackLoggedCause = null;
                var planned = selection.CommittedSlot!;
                await SavePlanEventAsync(services, "Planentscheidung fuer die Viertelstunde festgeschrieben.",
                    new { planned.TimeSlot, planned.Targets, planned.ChargeSource, planned.PricePerKwh, planned.Reason, planned.FollowsDemand }, token);
                break;
        }
        return (selection.Command, selectedAt, null, []);
    }

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
        services.GetService<ScopedSettingReadCache>()?.Enable();
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
        // Same status, error type and text (numbers ignored: measured watts differ from tick to tick) is the same
        // cause: written at once the first time, then once per interval with the number of repeats left out. A
        // different cause is written immediately.
        var eventKey = $"{state.Current.Status}|{error.GetType().Name}|{RepeatedEventThrottle.WithoutNumbers(Detail(error))}";
        if (!repeatedEvents.ShouldWrite(eventKey, clock.UtcNow, out var suppressed)) return;
        try { await LogAsync(services, timeout.Token, error.GetType().Name, suppressed); }
        catch (Exception logError) { logger.LogWarning("Gemeinsamer Fehlerstatus konnte nicht gespeichert werden. Fehlertyp={ErrorType}", logError.GetType().Name); }
    }

    private async Task LogAsync(IServiceProvider services, CancellationToken token, string? errorType = null, int suppressedRepeats = 0)
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
            current.UpdatedAtUtc, "JointBatteryExecution", errorType is null ? "Information" : "Error", current.Message + RepeatNote(suppressedRepeats),
            JsonSerializer.Serialize(errorType is null ? new { Execution = current, ErrorType = errorType }
                : (object)new { Execution = current, ErrorType = errorType, SuppressedRepeats = suppressedRepeats })), token);
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

/// <summary>
/// Limits repeated identical events: the first occurrence of a cause is written at once, afterwards one per interval
/// that carries the number of repeats left out in between. In-memory only; after a restart every cause is new again.
/// </summary>
public sealed class RepeatedEventThrottle(TimeSpan interval, int capacity = 32)
{
    private readonly Dictionary<string, (DateTimeOffset WrittenAtUtc, int Suppressed)> causes = [];

    /// <summary>Numbers (measured watts, counters) vary between repeats of the same cause and are not part of it.</summary>
    public static string WithoutNumbers(string? text) =>
        text is null ? "" : System.Text.RegularExpressions.Regex.Replace(text, @"-?\d+([.,]\d+)?", "#");

    public bool ShouldWrite(string cause, DateTimeOffset nowUtc, out int suppressedRepeats)
    {
        suppressedRepeats = 0;
        if (causes.TryGetValue(cause, out var known))
        {
            // A clock set back must not silence a cause for longer than the interval.
            var elapsed = nowUtc - known.WrittenAtUtc;
            if (elapsed >= TimeSpan.Zero && elapsed < interval)
            {
                causes[cause] = (known.WrittenAtUtc, known.Suppressed + 1);
                return false;
            }
            suppressedRepeats = known.Suppressed;
        }
        // Bounded memory: ever-changing texts must not grow the table; the longest-silent cause is forgotten first.
        else if (causes.Count >= capacity) causes.Remove(causes.MinBy(c => c.Value.WrittenAtUtc).Key);
        causes[cause] = (nowUtc, 0);
        return true;
    }
}
