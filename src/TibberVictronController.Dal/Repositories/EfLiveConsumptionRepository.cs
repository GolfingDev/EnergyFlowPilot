using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfLiveConsumptionRepository : ILiveConsumptionRepository
{
    private readonly ControllerDbContext dbContext;

    public EfLiveConsumptionRepository(ControllerDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task SaveSampleAsync(LiveConsumptionSample sample, CancellationToken cancellationToken = default)
    {
        dbContext.LiveConsumptionSamples.Add(new LiveConsumptionSampleEntity
        {
            MeasuredAtUtc = sample.MeasuredAtUtc,
            HouseConsumptionWatts = sample.HouseConsumptionWatts,
            GridPowerWatts = sample.GridPowerWatts,
            BatteryPowerWatts = sample.BatteryPowerWatts,
            BatterySocPercent = sample.BatterySocPercent,
            PvProductionWatts = sample.PvProductionWatts
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LiveConsumptionSample>> GetSamplesAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Startzeitpunkt fuer Live-Samples muss in UTC angegeben sein.", nameof(startsAtUtc));
        }

        if (endsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Endzeitpunkt fuer Live-Samples muss in UTC angegeben sein.", nameof(endsAtUtc));
        }

        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("Der Endzeitpunkt fuer Live-Samples muss nach dem Startzeitpunkt liegen.", nameof(endsAtUtc));
        }

        return await dbContext.LiveConsumptionSamples
            .Where(sample => sample.MeasuredAtUtc >= startsAtUtc && sample.MeasuredAtUtc < endsAtUtc)
            .OrderBy(sample => sample.MeasuredAtUtc)
            .Select(sample => new LiveConsumptionSample(
                sample.HouseConsumptionWatts,
                sample.MeasuredAtUtc,
                sample.GridPowerWatts,
                sample.BatteryPowerWatts,
                sample.BatterySocPercent,
                sample.PvProductionWatts))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default)
    {
        if (thresholdUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Bereinigungszeitpunkt fuer Live-Samples muss in UTC angegeben sein.", nameof(thresholdUtc));
        }

        var oldSamples = await dbContext.LiveConsumptionSamples
            .Where(sample => sample.MeasuredAtUtc < thresholdUtc)
            .ToListAsync(cancellationToken);

        dbContext.LiveConsumptionSamples.RemoveRange(oldSamples);
        await dbContext.SaveChangesAsync(cancellationToken);

        return oldSamples.Count;
    }
}
