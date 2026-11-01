using TibberVictronController.Api.Authentication;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Calendar;

namespace TibberVictronController.Api.EvCharging;

/// <summary>Calendar of one mailbox, read with the credentials of the Entra sign-in app.</summary>
public sealed class GraphCalendarSource(GraphCalendarClient client, ControllerAuthenticationOptions authentication) : ICalendarSource
{
    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(string mailbox, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        if (!authentication.IsEntra)
            throw new InvalidOperationException("Der Kalender braucht die Entra-Anmeldung (Mandant, Client-ID und Client-Secret in der Umgebungsdatei).");
        return await client.GetEventsAsync(new GraphCredentials(authentication.TenantId!, authentication.ClientId!, authentication.ClientSecret!),
            mailbox, fromUtc, toUtc, cancellationToken);
    }
}

/// <summary>Result of the last calendar sync for the car page; one sync at a time.</summary>
public sealed class CalendarSyncState
{
    private readonly object gate = new();
    public SemaphoreSlim Lock { get; } = new(1, 1);
    private CalendarSyncResult? lastResult;
    private string? lastError;
    private DateTimeOffset? lastAttemptAtUtc;

    public (CalendarSyncResult? Result, string? Error, DateTimeOffset? AttemptAtUtc) Current
    {
        get { lock (gate) return (lastResult, lastError, lastAttemptAtUtc); }
    }

    public void Succeeded(CalendarSyncResult result)
    {
        lock (gate) (lastResult, lastError, lastAttemptAtUtc) = (result, null, result.SyncedAtUtc);
    }

    /// <summary>Removes an appointment from the open list once the operator chose its place.</summary>
    public void Resolved(string eventId)
    {
        lock (gate)
        {
            if (lastResult is not null)
                lastResult = lastResult with { Unresolved = lastResult.Unresolved.Where(entry => entry.EventId != eventId).ToArray() };
        }
    }

    public void Failed(string error, DateTimeOffset atUtc)
    {
        lock (gate) (lastError, lastAttemptAtUtc) = (error, atUtc);
    }
}

/// <summary>Runs one sync if the calendar is switched on; shared by the background loop and "sync now".</summary>
public sealed class CalendarSyncRunner(CalendarTripSyncService sync, CalendarSyncState state, IControllerSettingStore settings, IUtcClock clock,
    ILogger<CalendarSyncRunner> logger)
{
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        if (!bool.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.EvCalendarEnabledKey, cancellationToken))?.Value, out var enabled) || !enabled)
            return false;
        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            state.Succeeded(await sync.SyncAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Kalenderabgleich fehlgeschlagen: {Reason}", exception.Message);
            state.Failed(exception.Message, clock.UtcNow);
        }
        finally
        {
            state.Lock.Release();
        }

        return true;
    }
}

public sealed class CalendarSyncBackgroundService(IServiceScopeFactory scopes, ILogger<CalendarSyncBackgroundService> logger) : BackgroundService
{
    // Appointments rarely change by the minute; 15 minutes keeps Graph and OpenStreetMap load small.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the database initialisation and the other services start first.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var rerouted = await scope.ServiceProvider.GetRequiredService<EvTripPlanningService>().RecalculateRoutesAsync(stoppingToken, outdatedOnly: true);
                if (rerouted > 0) logger.LogInformation("{Count} Fahrten mit der aktuellen Routenwahl neu berechnet.", rerouted);
                await scope.ServiceProvider.GetRequiredService<CalendarSyncRunner>().RunAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Kalenderabgleich unerwartet fehlgeschlagen.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
