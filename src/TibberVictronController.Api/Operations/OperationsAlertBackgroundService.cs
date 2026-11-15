using System.Text.Json;
using TibberVictronController.Api.Push;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Operations;

namespace TibberVictronController.Api.Operations;

/// <summary>
/// Tells people when control or its safeguards silently stop: once a minute it judges the observed state and pushes a
/// message when a problem has lasted its hold time, a reminder every six hours and one message when it is over. It
/// writes nothing to hardware. Its state lives in memory: after a restart a problem that still exists alerts again
/// after its hold time. A dead process or a dead Raspberry Pi cannot alert about itself.
/// </summary>
public sealed class OperationsAlertBackgroundService(IServiceScopeFactory scopes, OperationsSnapshotReader reader, IUtcClock clock,
    ILogger<OperationsAlertBackgroundService> logger) : BackgroundService
{
    public const string EventCategory = "OperationsAlert";

    // A message that could not be delivered for this long is dropped by the push service; the next reminder follows anyway.
    private static readonly TimeSpan MessageTimeToLive = TimeSpan.FromHours(6);
    private static readonly TimeZoneInfo DisplayZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private OperationsAlertState state = OperationsAlertState.Empty;
    private DateTimeOffset? observedSinceUtc;
    // Messages whose failed delivery is already in the log; a failure lasting hours is one log line, not one per minute.
    private readonly HashSet<string> reportedDeliveryProblems = new(StringComparer.Ordinal);

    /// <summary>Wait before the first evaluation: the controller connects to its devices first.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Pause between two evaluations; also kept after a failed one.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                // Only a cancelled stopping token ends the loop. A push or database timeout is an
                // OperationCanceledException too; if it left ExecuteAsync, the host would stop the whole process,
                // including the battery control.
                try { await EvaluateOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    // Push and database errors may carry addresses; the type is enough to see that the run failed.
                    logger.LogWarning("Betriebswarnungen konnten nicht ausgewertet werden. Fehlertyp={ErrorType}", exception.GetType().Name);
                }
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>One evaluation: read, judge, store starts and ends as events, send what is due.</summary>
    public async Task EvaluateOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var settings = services.GetRequiredService<IControllerSettingStore>();
        async Task<bool> Flag(string key, bool fallback) =>
            bool.TryParse((await settings.GetSettingAsync(key, cancellationToken))?.Value, out var value) ? value : fallback;

        if (!await Flag(ControllerSettingDefaults.OperationsAlertsEnabledKey, true))
        {
            // Switched off: forget everything, so switching on again starts with fresh hold times and no stale end messages.
            state = OperationsAlertState.Empty;
            observedSinceUtc = null;
            reportedDeliveryProblems.Clear();
            return;
        }

        var now = clock.UtcNow;
        observedSinceUtc ??= now;
        // Dry-run with joint control switched on is the deliberate shadow mode, not a fault.
        var jointExpected = await Flag(ControllerSettingDefaults.JointBatteryExecutionEnabledKey, false) &&
            !await Flag(ControllerSettingDefaults.VictronDryRunKey, true);
        var snapshot = reader.Read(now, jointExpected, observedSinceUtc.Value);
        var evaluation = OperationsAlertEvaluator.Evaluate(state, OperationsAlertConditions.Evaluate(snapshot, now, DisplayZone), now);
        // Kept before anything is stored or sent: a failure below must not make the alert start over.
        state = evaluation.State;

        var events = services.GetRequiredService<IOperationalEventRepository>();
        foreach (var alert in evaluation.Started)
            await SaveEventAsync(events, alert, "Warning", $"Betriebswarnung: {alert.Title}. {alert.Detail}", now, cancellationToken);
        foreach (var alert in evaluation.Ended)
            await SaveEventAsync(events, alert, "Information", $"Betriebswarnung beendet: {alert.Title}.", now, cancellationToken);

        if (evaluation.Due.Count == 0) return;
        var sender = services.GetRequiredService<IOperationsAlertSender>();
        foreach (var notification in evaluation.Due)
            await SendAsync(sender, notification, cancellationToken);
    }

    // An undelivered message stays due in the state and is offered again at the next evaluation.
    private async Task SendAsync(IOperationsAlertSender sender, OperationsAlertNotification notification, CancellationToken cancellationToken)
    {
        var key = $"{notification.Kind}|{notification.Alert.Id}";
        var text = OperationsAlertMessage.Compose(notification, DisplayZone);
        // One tag per alert: a reminder or the recovery replaces the earlier notification on the device.
        var message = new PushMessage("operations-alert", text.Title, text.Body, "efp-alert-" + notification.Alert.Id, "/");
        int delivered, failed;
        try
        {
            (delivered, failed) = await sender.SendAsync(message, MessageTimeToLive, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (reportedDeliveryProblems.Add(key))
                logger.LogWarning("Betriebswarnung \"{Title}\" konnte nicht per Push gesendet werden; neuer Versuch jede Minute. Fehlertyp={ErrorType}",
                    text.Title, exception.GetType().Name);
            return;
        }

        if (delivered == 0)
        {
            if (reportedDeliveryProblems.Add(key))
                logger.LogWarning(failed > 0
                        ? "Betriebswarnung \"{Title}\" wurde von keinem Geraet angenommen; neuer Versuch jede Minute."
                        : "Betriebswarnung \"{Title}\" nicht gesendet: auf keinem Geraet sind Benachrichtigungen eingeschaltet.", text.Title);
            return;
        }

        // Delivered to at least one device counts: repeating it for the others would spam the devices that have it.
        reportedDeliveryProblems.Remove(key);
        state = OperationsAlertEvaluator.Acknowledge(state, notification, clock.UtcNow);
        logger.LogInformation("Betriebswarnung \"{Title}\" per Push gesendet: {Delivered} zugestellt, {Failed} fehlgeschlagen.", text.Title, delivered, failed);
    }

    // The event is the durable trace of an alert; if it cannot be stored, the push still goes out.
    private async Task SaveEventAsync(IOperationalEventRepository events, OperationsAlert alert, string severity, string message, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await events.SaveEventAsync(new OperationalEvent(Guid.NewGuid(), now, EventCategory, severity, message,
                JsonSerializer.Serialize(new { alert.Id, alert.SinceUtc, alert.EndedAtUtc })), cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Betriebswarnung \"{Title}\" konnte nicht als Ereignis gespeichert werden. Fehlertyp={ErrorType}", alert.Title, exception.GetType().Name);
        }
    }
}
