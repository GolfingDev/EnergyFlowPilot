using System.Net.WebSockets;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

/// <summary>
/// Keeps the controller's current limit alive at the wallbox: sends it on change, renews it before it expires and
/// repeats failed commands. Every limit expires by itself (rule 1), so a renewal that comes too late lets the wallbox
/// charge at full power; the timing below is chosen so that several retries fit before the expiry.
/// A command that fails never throws here (only a cancelled caller token does): a wallbox dropping its connection
/// must not end the control loop.
/// </summary>
public sealed class EvChargerLimitKeeper(IEvChargerControl control, IUtcClock clock)
{
    public static readonly TimeSpan LimitValidity = TimeSpan.FromMinutes(15);

    // An unanswered command blocks for up to a minute (30 s per connector) and is repeated with the next cycle, so
    // one failed attempt costs about 1.5 minutes. Seven minutes leave room for three to four attempts before the
    // limit expires; in normal operation the limit is renewed every eight minutes.
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(7);

    // The wallbox said no: asking again every cycle would not change its mind.
    public static readonly TimeSpan RetryAfterRefusal = TimeSpan.FromMinutes(5);

    // No answer or connection lost: the command may simply have been lost. Shorter than the 30 s control cycle, so
    // the very next cycle repeats it.
    public static readonly TimeSpan RetryAfterNoAnswer = TimeSpan.FromSeconds(25);

    private bool limitActive;
    private double? appliedAmps;
    private DateTimeOffset appliedUntilUtc;
    private DateTimeOffset retryAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset releaseRetryAtUtc = DateTimeOffset.MinValue;

    /// <summary>True only while a confirmed limit is still valid; a limit that ran out is no limit any more.</summary>
    public bool LimitActive => limitActive && clock.UtcNow < appliedUntilUtc;

    public double? AppliedAmps => LimitActive ? appliedAmps : null;

    public DateTimeOffset? AppliedUntilUtc => LimitActive ? appliedUntilUtc : null;

    /// <summary>The last command problem (German, for the car page); null after an accepted command.</summary>
    public string? CommandProblem { get; private set; }

    /// <summary>Makes the wallbox follow the wanted current; null means no limit (release).</summary>
    public async Task ApplyAsync(double? amps, CancellationToken cancellationToken)
    {
        if (amps is null)
        {
            if (LimitActive) await ReleaseAsync(cancellationToken);
            return;
        }

        var now = clock.UtcNow;
        var unchanged = LimitActive && appliedAmps == amps && appliedUntilUtc - now > RenewBefore;
        if (unchanged || now < retryAtUtc) return;

        var (result, problem, unanswered) = await SendAsync(() => control.LimitCurrentAsync(amps.Value, LimitValidity, cancellationToken), cancellationToken);
        if (result is { Accepted: true })
        {
            (limitActive, appliedAmps, appliedUntilUtc, CommandProblem) = (true, amps, result.ValidUntilUtc ?? now + LimitValidity, null);
            return;
        }

        // The clock is read again: an unanswered command has already taken up to a minute.
        retryAtUtc = clock.UtcNow + (unanswered ? RetryAfterNoAnswer : RetryAfterRefusal);
        // An older limit with another current may still be in force; without one the car page must not suggest a limit.
        CommandProblem = LimitActive
            ? problem
            : $"{problem} Derzeit ist keine Begrenzung bestätigt: die Wallbox lädt möglicherweise nach eigener Einstellung.";
    }

    /// <summary>Removes the controller's limit, so the wallbox charges by its own setting.</summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (clock.UtcNow < releaseRetryAtUtc) return;
        var (result, problem, unanswered) = await SendAsync(() => control.ClearLimitAsync(cancellationToken), cancellationToken);
        if (result is { Accepted: true })
        {
            (limitActive, appliedAmps, CommandProblem) = (false, null, null);
            return;
        }

        CommandProblem = problem;
        if (unanswered)
        {
            // The limit may still be in force (for example 0 A while full power is wanted): try again with the next
            // cycle, at most until the limit expires by itself.
            releaseRetryAtUtc = clock.UtcNow + RetryAfterNoAnswer;
            return;
        }

        // The wallbox refuses to clear: the limit expires by itself within its validity.
        (limitActive, appliedAmps) = (false, null);
    }

    private static async Task<(EvChargerCommandResult? Result, string Problem, bool Unanswered)> SendAsync(
        Func<Task<EvChargerCommandResult>> command, CancellationToken cancellationToken)
    {
        try
        {
            var result = await command();
            return (result, result.Description, EvChargerCommandStatus.IsUnanswered(result.Status));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Not the caller's cancellation: the connection ended while the command waited for its answer.
            return (null, "Die Verbindung zur Wallbox wurde während des Befehls getrennt.", true);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or TimeoutException)
        {
            return (null, $"Der Befehl konnte nicht an die Wallbox gesendet werden: {exception.Message}", true);
        }
        catch (InvalidOperationException exception)
        {
            // "Die Wallbox ist nicht verbunden.": it reconnects by itself, so this is no refusal either.
            return (null, exception.Message, true);
        }
        catch (ArgumentException exception)
        {
            // A current or validity the adapter does not accept stays wrong; treated like a refusal.
            return (null, exception.Message, false);
        }
    }
}
