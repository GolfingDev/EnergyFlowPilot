using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfPvForecastSnapshotRepository(ControllerDbContext dbContext) : IPvForecastSnapshotRepository
{
    // Older snapshots are not needed for a two-week correction.
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public async Task UpsertAsync(IReadOnlyCollection<PvForecastSnapshot> snapshots, DateTimeOffset capturedAtUtc, CancellationToken cancellationToken = default)
    {
        if (snapshots.Count == 0) return;
        var starts = snapshots.Select(snapshot => snapshot.SlotStartUtc).ToArray();
        var existing = await dbContext.PvForecastSnapshots.Where(entity => starts.Contains(entity.SlotStartUtc))
            .ToDictionaryAsync(entity => entity.SlotStartUtc, cancellationToken);
        foreach (var snapshot in snapshots)
        {
            if (!existing.TryGetValue(snapshot.SlotStartUtc, out var entity))
            {
                entity = new PvForecastSnapshotEntity { SlotStartUtc = snapshot.SlotStartUtc };
                dbContext.PvForecastSnapshots.Add(entity);
            }

            entity.ForecastKwh = (double)snapshot.ForecastKwh;
            entity.CapturedAtUtc = capturedAtUtc;
        }

        var threshold = capturedAtUtc - Retention;
        await dbContext.PvForecastSnapshots.Where(entity => entity.SlotStartUtc < threshold).ExecuteDeleteAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PvForecastSnapshot>> GetAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.PvForecastSnapshots.AsNoTracking()
            .Where(entity => entity.SlotStartUtc >= fromUtc && entity.SlotStartUtc < toUtc)
            .ToListAsync(cancellationToken);
        return entities.Select(entity => new PvForecastSnapshot(entity.SlotStartUtc, (decimal)entity.ForecastKwh)).ToArray();
    }
}

public sealed class EfCarSampleRepository(ControllerDbContext dbContext) : ICarSampleRepository
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(400);

    public async Task AddAsync(CarSample sample, CancellationToken cancellationToken = default)
    {
        dbContext.CarSamples.Add(new CarSampleEntity { MeasuredAtUtc = sample.MeasuredAtUtc, SocPercent = sample.SocPercent, OdometerKm = sample.OdometerKm });
        var threshold = sample.MeasuredAtUtc - Retention;
        await dbContext.CarSamples.Where(entity => entity.MeasuredAtUtc < threshold).ExecuteDeleteAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CarSample>> GetSinceAsync(DateTimeOffset fromUtc, CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.CarSamples.AsNoTracking()
            .Where(entity => entity.MeasuredAtUtc >= fromUtc)
            .OrderBy(entity => entity.MeasuredAtUtc)
            .ToListAsync(cancellationToken);
        return entities.Select(entity => new CarSample(entity.MeasuredAtUtc, entity.SocPercent, entity.OdometerKm)).ToArray();
    }
}
