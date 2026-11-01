using TibberVictronController.Api.Authentication;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Calendar;

namespace TibberVictronController.Api.EvCharging;

/// <summary>Calendar of the configured mailbox, read with the credentials of the Entra sign-in app.</summary>
public sealed class GraphCalendarSource(GraphCalendarClient client, ControllerAuthenticationOptions authentication, IControllerSettingStore settings)
    : ICalendarSource
{
    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
    {
        if (!authentication.IsEntra)
            throw new InvalidOperationException("Der Kalender braucht die Entra-Anmeldung (Mandant, Client-ID und Client-Secret in der Umgebungsdatei).");
        var mailbox = (await settings.GetSettingAsync(ControllerSettingDefaults.EvCalendarMailboxKey, cancellationToken))?.Value;
        if (string.IsNullOrWhiteSpace(mailbox) || mailbox == ControllerSettingDefaults.EvCalendarMailboxNotConfigured)
            throw new InvalidOperationException("Kein Postfach fuer den Kalender eingetragen (Einstellungen, Kalender).");
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
                await scope.ServiceProvider.GetRequiredService<CalendarSyncRunner>().RunAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Kalenderabgleich unerwartet fehlgeschlagen.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
