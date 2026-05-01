using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;

namespace TibberVictronController.Dal.Persistence;

public sealed class ControllerDbInitializer
{
    private readonly ControllerDbContext dbContext;

    public ControllerDbInitializer(ControllerDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task InitializeAsync(DateTimeOffset initializedAtUtc, CancellationToken cancellationToken = default)
    {
        if (initializedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Initialisierungszeitpunkt der Datenbank muss in UTC angegeben sein.", nameof(initializedAtUtc));
        }

        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await SeedMissingDefaultSettingsAsync(initializedAtUtc, cancellationToken);
    }

    private async Task SeedMissingDefaultSettingsAsync(
        DateTimeOffset initializedAtUtc,
        CancellationToken cancellationToken)
    {
        var existingSettingKeys = await dbContext.ControllerSettings
            .Select(setting => setting.Key)
            .ToListAsync(cancellationToken);
        var existingSettingKeySet = existingSettingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingSettings = ControllerSettingDefaults
            .CreateDefaultSettings(initializedAtUtc)
            .Where(setting => !existingSettingKeySet.Contains(setting.Key))
            .Select(MapToEntity)
            .ToArray();

        if (missingSettings.Length == 0)
        {
            return;
        }

        dbContext.ControllerSettings.AddRange(missingSettings);
        await dbContext.SaveChangesAsync(cancellationToken);
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
