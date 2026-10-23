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
    private DateTimeOffset lastLoggedAt;
    private string? lastLoggedStatus;
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
                    logger.LogWarning("Gemeinsame Ausfuehrung gestoppt. Fehlertyp={ErrorType}", error.GetType().Name);
                    try { await HandleFailureAsync(error); }
                    catch (Exception cleanup) { logger.LogWarning("Gemeinsame Ruecknahme bleibt ausstehend. Fehlertyp={ErrorType}", cleanup.GetType().Name); }
                }
                await watchdog.PulseAsync(stoppingToken);
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

    public async Task TickAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var settings = services.GetRequiredService<IControllerSettingStore>();
        var clock = services.GetRequiredService<IUtcClock>();
        async Task<bool> Flag(string key) => bool.TryParse((await settings.GetSettingAsync(key, token))?.Value, out var value) && value;
        using var lease = await ownership.AcquirePublicationAsync(token);
        var enabled = await Flag(ControllerSettingDefaults.JointBatteryExecutionEnabledKey);
        var pending = await Flag(ControllerSettingDefaults.JointBatteryRecoveryPendingKey);
        var dryRunSetting = (await settings.GetSettingAsync(ControllerSettingDefaults.VictronDryRunKey, token))?.Value;
        var dryRun = !bool.TryParse(dryRunSetting, out var dry) || dry;
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(token);
        cycle.CancelAfter(TimeSpan.FromSeconds(3));
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
        // Claim ownership before any hardware writes, and cancel cached single-battery refreshes under the same lock.
        if (!pending) await SaveRecoveryAsync(settings, clock, true, cycle.Token);
        ownership.JointControlActive = true;
        ownership.Clear();
        if (!watchdog.IsSupervised)
            throw new InvalidOperationException("Gemeinsame Hardwareausfuehrung benoetigt den installierten systemd-Watchdog und Ruecknahme-Helfer.");
        if (!await Flag(ControllerSettingDefaults.E3dcRscpEnabledKey))
            throw new InvalidOperationException("Lokaler E3/DC-Zugang ist deaktiviert.");
        var runtime = services.GetRequiredService<JointBatteryRuntimeState>().Current;
        var plan = runtime.Plan ?? throw new InvalidOperationException("Ein gueltiger gemeinsamer Plan fehlt.");
        if (clock.UtcNow - plan.CalculatedAtUtc > TimeSpan.FromSeconds(75))
            throw new InvalidOperationException("Die gemeinsame Planung ist veraltet.");
        var slot = plan.Slots.SingleOrDefault(s => s.TimeSlot.StartsAtUtc <= clock.UtcNow && s.TimeSlot.EndsAtUtc > clock.UtcNow)
            ?? throw new InvalidOperationException("Der aktuelle gemeinsame Planzeitabschnitt fehlt.");
        if (slot.IsPriceEstimated && slot.ChargeSource == BatteryChargeSource.Grid)
            throw new InvalidOperationException("Geschaetzte Preise erlauben keine Netzladung.");
        var validUntil = slot.TimeSlot.EndsAtUtc < plan.CalculatedAtUtc.AddSeconds(75) ? slot.TimeSlot.EndsAtUtc : plan.CalculatedAtUtc.AddSeconds(75);
        var command = new JointBatteryCommand(slot.Targets.Single(t => t.Id == "victron").SignedPowerWatts,
            slot.Targets.Single(t => t.Id == "e3dc").SignedPowerWatts, slot.ChargeSource, validUntil, slot.Reason);
        await services.GetRequiredService<JointBatteryExecutionCoordinator>().StepAsync(command, cycle.Token);
        await LogAsync(services, cycle.Token);
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
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
        state.Set(new(restored ? "Blocked" : "Restoring", restored ? "Gemeinsame Steuerung nach Fehler gestoppt; erneuter Versuch fruehestens nach 30 Sekunden." :
            "Gemeinsame Ruecknahme noch nicht bestaetigt; Einzelakku-Steuerung bleibt gesperrt.", clock.UtcNow, ownership.JointControlActive));
        try { await LogAsync(services, timeout.Token, error.GetType().Name); }
        catch (Exception logError) { logger.LogWarning("Gemeinsamer Fehlerstatus konnte nicht gespeichert werden. Fehlertyp={ErrorType}", logError.GetType().Name); }
    }

    private async Task LogAsync(IServiceProvider services, CancellationToken token, string? errorType = null)
    {
        var current = state.Current;
        if (errorType is null && current.Status == lastLoggedStatus && current.UpdatedAtUtc - lastLoggedAt < TimeSpan.FromSeconds(30)) return;
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
        lastLoggedStatus = current.Status; lastLoggedAt = current.UpdatedAtUtc;
    }
}
