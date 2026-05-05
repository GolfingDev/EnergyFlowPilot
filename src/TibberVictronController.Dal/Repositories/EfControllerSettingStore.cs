using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfControllerSettingStore : IControllerSettingStore
{
    private readonly ControllerDbContext dbContext;

    public EfControllerSettingStore(ControllerDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.ControllerSettings
            .OrderBy(setting => setting.Key)
            .Select(setting => MapToDomain(setting))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        var setting = await dbContext.ControllerSettings
            .SingleOrDefaultAsync(storedSetting => storedSetting.Key == key, cancellationToken);

        return setting is null ? null : MapToDomain(setting);
    }

    public async Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default)
    {
        var existingSetting = await dbContext.ControllerSettings
            .SingleOrDefaultAsync(storedSetting => storedSetting.Key == setting.Key, cancellationToken);

        if (existingSetting is null)
        {
            dbContext.ControllerSettings.Add(MapToEntity(setting));
        }
        else
        {
            existingSetting.Value = setting.Value;
            existingSetting.Sensitivity = setting.Sensitivity;
            existingSetting.UpdatedAtUtc = setting.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ControllerSetting MapToDomain(ControllerSettingEntity setting)
    {
        return new ControllerSetting(setting.Key, setting.Value, setting.Sensitivity, setting.UpdatedAtUtc);
    }

    private static ControllerSettingEntity MapToEntity(ControllerSetting setting)
    {
        return new ControllerSettingEntity
        {
            Key = setting.Key,
            Value = setting.Value,
            Sensitivity = setting.Sensitivity,
            UpdatedAtUtc = setting.UpdatedAtUtc
        };
    }
}
