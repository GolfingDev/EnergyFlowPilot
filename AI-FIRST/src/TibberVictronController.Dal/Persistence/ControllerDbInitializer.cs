using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Globalization;
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
        await EnsureMissingTablesAsync(cancellationToken);
        await SeedMissingDefaultSettingsAsync(initializedAtUtc, cancellationToken);
    }

    private async Task EnsureMissingTablesAsync(CancellationToken cancellationToken)
    {
        var databaseCreator = dbContext.GetService<IRelationalDatabaseCreator>();
        var createScript = dbContext.Database.GenerateCreateScript();

        foreach (var tableName in new[] { "BatterySavingsDailySummaries" })
        {
            if (await databaseCreator.HasTablesAsync(cancellationToken) &&
                await TableExistsAsync(tableName, cancellationToken))
            {
                continue;
            }

            var tableScript = ExtractCreateTableScript(createScript, tableName);

            if (!string.IsNullOrWhiteSpace(tableScript))
            {
                await dbContext.Database.ExecuteSqlRawAsync(tableScript, cancellationToken);
            }
        }
    }

    private async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeConnectionAfterQuery = connection.State != System.Data.ConnectionState.Open;

        if (closeConnectionAfterQuery)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$tableName";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(cancellationToken);

            return Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0;
        }
        finally
        {
            if (closeConnectionAfterQuery)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static string ExtractCreateTableScript(string createScript, string tableName)
    {
        var tableMarker = $"CREATE TABLE \"{tableName}\"";
        var tableStartIndex = createScript.IndexOf(tableMarker, StringComparison.OrdinalIgnoreCase);

        if (tableStartIndex < 0)
        {
            return string.Empty;
        }

        var nextTableIndex = createScript.IndexOf("CREATE TABLE", tableStartIndex + tableMarker.Length, StringComparison.OrdinalIgnoreCase);
        var scriptEndIndex = nextTableIndex < 0 ? createScript.Length : nextTableIndex;

        return createScript[tableStartIndex..scriptEndIndex];
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
