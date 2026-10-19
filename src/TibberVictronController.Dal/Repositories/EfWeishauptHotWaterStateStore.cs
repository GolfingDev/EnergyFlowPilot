using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfWeishauptHotWaterStateStore(ControllerDbContext dbContext) : IWeishauptHotWaterStateStore
{
    public async Task<WeishauptHotWaterState> LoadAsync(CancellationToken cancellationToken)
    {
        var entity = await dbContext.WeishauptHotWaterControlStates.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (entity is null) return new WeishauptHotWaterState();
        return JsonSerializer.Deserialize<WeishauptHotWaterState>(entity.StateJson)
            ?? throw new InvalidOperationException("Weishaupt-Steuerzustand konnte nicht gelesen werden.");
    }

    public async Task SaveAsync(WeishauptHotWaterState state, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WeishauptHotWaterControlStates.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (entity is null)
        {
            entity = new WeishauptHotWaterControlStateEntity { Id = 1 };
            dbContext.WeishauptHotWaterControlStates.Add(entity);
        }
        entity.StateJson = JsonSerializer.Serialize(state);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
