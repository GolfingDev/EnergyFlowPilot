using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

/// <summary>Read-only shadow worker with bounded polling and reconnect backoff, isolated from production control.</summary>
public sealed class JointBatteryShadowBackgroundService(IServiceScopeFactory scopes, JointBatteryRuntimeState state,
    ILogger<JointBatteryShadowBackgroundService> logger) : BackgroundService
{
    private DateTimeOffset? lastLoggedSlot;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                failures = Math.Min(3, failures + 1);
                // Neither configuration values nor exception messages are logged: connection/auth errors may contain secrets.
                logger.LogWarning("Gemeinsame Akku-Vorschau fehlgeschlagen. Fehlertyp={ErrorType}", exception.GetType().Name);
                state.Set(new("Blocked", "Die gemeinsame Vorschau ist wegen fehlender oder ungueltiger Daten gesperrt (" + exception.GetType().Name + ").",
                    DateTimeOffset.UtcNow, state.Current.LocalTelemetry, null));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(failures == 0 ? 30 : Math.Min(60, 15 * (1 << failures))), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task PollAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var clock = services.GetRequiredService<IUtcClock>();
        var settings = services.GetRequiredService<IControllerSettingStore>();
        async Task<bool> Enabled(string key) => bool.TryParse((await settings.GetSettingAsync(key, token))?.Value, out var value) && value;
        if (!await Enabled(ControllerSettingDefaults.E3dcRscpEnabledKey))
        {
            state.Set(new("Disabled", "Der lokale E3/DC-Lesezugriff ist deaktiviert.", clock.UtcNow, null, null));
            return;
        }
        var local = await services.GetRequiredService<IE3dcTelemetrySource>().ReadAsync(token);
        if (!await Enabled(ControllerSettingDefaults.JointBatteryEnabledKey) && !await Enabled(ControllerSettingDefaults.JointBatteryExecutionEnabledKey))
        {
            state.Set(new("Monitoring", "Lokale E3/DC-Messwerte verfuegbar; gemeinsame Vorschau deaktiviert.", clock.UtcNow, local, null));
            return;
        }
        // Keep the previous, still time-bounded plan available while its replacement is calculated.
        var plan = await services.GetRequiredService<JointBatteryShadowCalculation>().CalculateAsync(local, token);
        // Logging is required for review, but only once per price slot. No simulated decision enters the production log.
        var first = plan.Slots.FirstOrDefault();
        if (first is not null && first.TimeSlot.StartsAtUtc != lastLoggedSlot)
        {
            await services.GetRequiredService<IOperationalEventRepository>().SaveEventAsync(new OperationalEvent(
                Guid.NewGuid(), clock.UtcNow, "JointBatteryShadow", "Information", "Gemeinsame Akku-Vorschau berechnet; keine Hardwarebefehle.",
                JsonSerializer.Serialize(new { first.TimeSlot, first.Targets, first.Reason, plan.MaximumGridImportWatts })), token);
            lastLoggedSlot = first.TimeSlot.StartsAtUtc;
        }
        state.Set(new("Shadow", "Gemeinsame Vorschau aktiv. Es werden keine Hardwarebefehle gesendet.", clock.UtcNow, local, plan));
    }
}
