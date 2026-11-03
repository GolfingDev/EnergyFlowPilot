using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfEvChargingSessionRepository(ControllerDbContext dbContext) : IEvChargingSessionRepository
{
    public async Task<EvChargingSession> StartAsync(EvChargingSessionStart start, CancellationToken cancellationToken = default)
    {
        // Chargers resend StartTransaction when the answer got lost; the same start must keep its transaction id.
        var existing = await dbContext.EvChargingSessions.FirstOrDefaultAsync(session =>
            session.ChargePointId == start.ChargePointId && session.ConnectorId == start.ConnectorId &&
            session.StartedAtUtc == start.StartedAtUtc && session.MeterStartWh == start.MeterStartWh, cancellationToken);
        if (existing is not null) return Map(existing);

        // A missed StopTransaction must not leave a session open forever on the same connector.
        var open = await dbContext.EvChargingSessions
            .Where(session => session.ChargePointId == start.ChargePointId && session.ConnectorId == start.ConnectorId && session.StoppedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var session in open)
        {
            session.StoppedAtUtc = start.StartedAtUtc;
            session.MeterStopWh = session.LastMeterWh ?? session.MeterStartWh;
            session.StopReason = "ReplacedByNewSession";
        }

        var entity = new EvChargingSessionEntity
        {
            ChargePointId = start.ChargePointId,
            ConnectorId = start.ConnectorId,
            IdTag = start.IdTag,
            StartedAtUtc = start.StartedAtUtc,
            MeterStartWh = start.MeterStartWh,
            LastMeterWh = start.MeterStartWh,
            LastMeterAtUtc = start.StartedAtUtc
        };
        dbContext.EvChargingSessions.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<EvChargingSession?> GetAsync(int transactionId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EvChargingSessions.AsNoTracking()
            .FirstOrDefaultAsync(session => session.TransactionId == transactionId, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task UpdateMeterAsync(int transactionId, double meterWh, DateTimeOffset measuredAtUtc, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EvChargingSessions.FirstOrDefaultAsync(session => session.TransactionId == transactionId, cancellationToken);
        if (entity is null || entity.StoppedAtUtc is not null) return;
        entity.LastMeterWh = meterWh;
        entity.LastMeterAtUtc = measuredAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<EvChargingSession?> StopAsync(int transactionId, double meterStopWh, DateTimeOffset stoppedAtUtc, string? reason, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EvChargingSessions.FirstOrDefaultAsync(session => session.TransactionId == transactionId, cancellationToken);
        if (entity is null) return null;
        // A repeated StopTransaction keeps the first, authoritative stop values.
        if (entity.StoppedAtUtc is null)
        {
            entity.StoppedAtUtc = stoppedAtUtc;
            entity.MeterStopWh = meterStopWh;
            entity.StopReason = reason;
            entity.LastMeterWh = meterStopWh;
            entity.LastMeterAtUtc = stoppedAtUtc;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Map(entity);
    }

    public async Task<IReadOnlyList<EvChargingSession>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCount), "Die Anzahl der Ladevorgaenge muss groesser als 0 sein.");
        var entities = await dbContext.EvChargingSessions.AsNoTracking()
            .OrderByDescending(session => session.StartedAtUtc)
            .Take(maxCount)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async Task MarkBaseChoiceAskedAsync(int transactionId, DateTimeOffset askedAtUtc, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EvChargingSessions.FirstOrDefaultAsync(session => session.TransactionId == transactionId, cancellationToken);
        if (entity is null || entity.BaseChoiceAskedAtUtc is not null) return;
        entity.BaseChoiceAskedAtUtc = askedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> SetBaseChargeModeAsync(int transactionId, EvBaseChargeMode mode, string decidedBy, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EvChargingSessions.FirstOrDefaultAsync(session => session.TransactionId == transactionId, cancellationToken);
        if (entity is null || entity.StoppedAtUtc is not null) return false;
        entity.BaseChargeMode = mode.ToString();
        entity.BaseChoiceBy = decidedBy.Length <= 200 ? decidedBy : decidedBy[..200];
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static EvChargingSession Map(EvChargingSessionEntity entity) => new(
        entity.TransactionId, entity.ChargePointId, entity.ConnectorId, entity.IdTag, entity.StartedAtUtc, entity.MeterStartWh,
        entity.StoppedAtUtc, entity.MeterStopWh, entity.StopReason, entity.LastMeterWh, entity.LastMeterAtUtc)
    {
        BaseChargeMode = Enum.TryParse<EvBaseChargeMode>(entity.BaseChargeMode, out var mode) ? mode : null,
        BaseChoiceAskedAtUtc = entity.BaseChoiceAskedAtUtc,
        BaseChoiceBy = entity.BaseChoiceBy
    };
}
