using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

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
            HouseConsumptionWatts = sample.HouseConsumptionWatts
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
