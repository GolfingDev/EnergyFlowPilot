using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Services;

/// <summary>Changes only owned setpoints, persisting intent before hardware I/O.</summary>
public sealed class WeishauptHotWaterControlService(
    IWeishauptHotWaterControl control,
    IWeishauptHotWaterStateStore stateStore,
    IOperationalEventRepository events)
{
    public async Task RunAsync(bool enabled, bool boostRequested, DateTimeOffset nowUtc, CancellationToken cancellationToken,
        WeishauptHotWaterBoostContext? context = null)
    {
        var state = await stateStore.LoadAsync(cancellationToken);
        if (!enabled && state.OriginalCelsius is null)
        {
            if (state.Suspended)
                await stateStore.SaveAsync(state with { Suspended = false }, cancellationToken);
            return;
        }
        if (state.Suspended || (state.OriginalCelsius is null &&
            (!boostRequested || nowUtc < state.NextBoostAtUtc))) return;

        var reading = await control.ReadAsync(cancellationToken);
        if (state.OriginalCelsius is { } original)
        {
            if (reading.DeviceIdentity != state.DeviceIdentity)
                throw new InvalidOperationException("Weishaupt-Zieladresse wurde bei aktiver Anhebung geaendert. Urspruengliche Verbindung zur Ruecknahme wiederherstellen.");

            if (reading.NormalSetPointCelsius != state.RaisedCelsius)
            {
                // A human change or a write that never arrived: relinquish ownership, never overwrite it.
                await AuditAsync(nowUtc, "Sollwert weicht von der eigenen Anhebung ab; Automatik wird pausiert.", cancellationToken);
                await stateStore.SaveAsync(ClearOwnership(EndEpisode(state, nowUtc, interrupted: true)) with
                {
                    Suspended = enabled && reading.NormalSetPointCelsius != original,
                    NextBoostAtUtc = nowUtc.AddMinutes(30)
                }, cancellationToken);
                return;
            }
            var elapsed = nowUtc - state.RaisedAtUtc!.Value;
            var finished = context?.TargetReached == true || (state.BoostUntilUtc is { } until && nowUtc >= until);
            if (enabled && !finished && boostRequested && elapsed < TimeSpan.FromHours(4)) return;
            if (enabled && !finished && !boostRequested && elapsed < TimeSpan.FromMinutes(10)) return;

            await ChangeAsync(reading, original, nowUtc, cancellationToken);
            await stateStore.SaveAsync(ClearOwnership(EndEpisode(state, nowUtc, interrupted: false)) with
                { NextBoostAtUtc = nowUtc.AddMinutes(30) }, cancellationToken);
            return;
        }

        if (reading.NormalSetPointCelsius is < 35m or > 50m)
            throw new InvalidOperationException("Weishaupt-Warmwassersollwert liegt ausserhalb des freigegebenen Ausgangsbereichs 35 bis 50 Grad.");
        var raised = reading.NormalSetPointCelsius + 5m;
        if (context is not null && (context.LatestTemperatureCelsius is null or < 0m or > 95m ||
            context.LatestTemperatureCelsius >= raised - 0.5m)) return;
        // Persist the original before sending: a timeout after a successful write must remain recoverable.
        var episode = context is null ? null : new WeishauptHotWaterEpisode
        {
            StartedAtUtc = nowUtc, OriginalCelsius = reading.NormalSetPointCelsius,
            RaisedCelsius = raised, Plan = context.Plan, Trigger = context.Trigger
        };
        state = state with
        {
            OriginalCelsius = reading.NormalSetPointCelsius, RaisedCelsius = raised,
            RaisedAtUtc = nowUtc, DeviceIdentity = reading.DeviceIdentity,
            BoostUntilUtc = context?.Plan.EndsAtUtc is { } end && end < nowUtc.AddHours(4) ? end : nowUtc.AddHours(4),
            Episodes = episode is null ? state.Episodes : state.Episodes.Append(episode).TakeLast(60).ToArray()
        };
        await stateStore.SaveAsync(state, cancellationToken);
        await ChangeAsync(reading, raised, nowUtc, cancellationToken);
        if (episode is not null)
            await stateStore.SaveAsync(state with { Episodes = state.Episodes.Select(e => e.Id == episode.Id
                ? e with { WriteConfirmed = true } : e).ToArray() }, cancellationToken);
    }

    private static WeishauptHotWaterState ClearOwnership(WeishauptHotWaterState state) => state with
    { OriginalCelsius = null, RaisedCelsius = null, RaisedAtUtc = null, DeviceIdentity = null, BoostUntilUtc = null };

    private static WeishauptHotWaterState EndEpisode(WeishauptHotWaterState state, DateTimeOffset now, bool interrupted) => state with
    {
        Episodes = state.Episodes.Select(e => e.StartedAtUtc == state.RaisedAtUtc && e.EndedAtUtc is null
            ? e with { EndedAtUtc = now, Interrupted = interrupted } : e).ToArray()
    };

    private async Task ChangeAsync(WeishauptHotWaterReading reading, decimal target, DateTimeOffset now, CancellationToken ct)
    {
        await AuditAsync(now, $"Warmwasser-Sollwertaenderung angefordert: {reading.NormalSetPointCelsius} auf {target} Grad.", ct);
        await control.SetNormalTemperatureAsync(reading, target, ct);
        await AuditAsync(now, $"Warmwasser-Sollwert {target} Grad durch Ruecklesen bestaetigt.", ct);
    }

    private Task AuditAsync(DateTimeOffset now, string message, CancellationToken ct) =>
        events.SaveEventAsync(new OperationalEvent(Guid.NewGuid(), now, "WeishauptHotWater", "Information", message, null), ct);
}
