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
        await EnsureLiveConsumptionSampleColumnsAsync(cancellationToken);
        await SeedMissingDefaultSettingsAsync(initializedAtUtc, cancellationToken);
    }

    private async Task EnsureMissingTablesAsync(CancellationToken cancellationToken)
    {
        var databaseCreator = dbContext.GetService<IRelationalDatabaseCreator>();

        foreach (var tableName in new[] { "BatterySavingsDailySummaries", "LiveConsumptionSamples", "ConsumptionDayProfiles" })
        {
            if (await databaseCreator.HasTablesAsync(cancellationToken) &&
                await TableExistsAsync(tableName, cancellationToken))
            {
                continue;
            }

            var tableScript = CreateTableScript(tableName);

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

    private async Task EnsureLiveConsumptionSampleColumnsAsync(CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync("LiveConsumptionSamples", cancellationToken))
        {
            return;
        }

        var existingColumns = await GetColumnNamesAsync("LiveConsumptionSamples", cancellationToken);
        var columnsToAdd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GridPowerWatts"] = "REAL NULL",
            ["BatteryPowerWatts"] = "REAL NULL",
            ["BatterySocPercent"] = "REAL NULL",
            ["PvProductionWatts"] = "REAL NULL"
        };

        foreach (var column in columnsToAdd)
        {
            if (existingColumns.Contains(column.Key))
            {
                continue;
            }

            var alterTableSql = $"""ALTER TABLE "LiveConsumptionSamples" ADD COLUMN "{column.Key}" {column.Value};""";
            await dbContext.Database.ExecuteSqlRawAsync(alterTableSql, cancellationToken);
        }
    }

    private async Task<HashSet<string>> GetColumnNamesAsync(string tableName, CancellationToken cancellationToken)
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
            command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(reader.GetString(1));
            }

            return result;
        }
        finally
        {
            if (closeConnectionAfterQuery)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static string CreateTableScript(string tableName)
    {
        if (!string.Equals(tableName, "BatterySavingsDailySummaries", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(tableName, "LiveConsumptionSamples", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(tableName, "ConsumptionDayProfiles", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return """
CREATE TABLE IF NOT EXISTS "ConsumptionDayProfiles" (
    "DayOfWeek" INTEGER NOT NULL,
    "SlotIndex" INTEGER NOT NULL,
    "AverageConsumptionWatts" REAL NOT NULL,
    "SampleCount" INTEGER NOT NULL,
    "UpdatedAtUtc" INTEGER NOT NULL,
    CONSTRAINT "PK_ConsumptionDayProfiles" PRIMARY KEY ("DayOfWeek", "SlotIndex")
);
CREATE INDEX IF NOT EXISTS "IX_ConsumptionDayProfiles_UpdatedAtUtc" ON "ConsumptionDayProfiles" ("UpdatedAtUtc");
""";
            }

            return """
CREATE TABLE IF NOT EXISTS "LiveConsumptionSamples" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_LiveConsumptionSamples" PRIMARY KEY AUTOINCREMENT,
    "MeasuredAtUtc" INTEGER NOT NULL,
    "HouseConsumptionWatts" REAL NOT NULL,
    "GridPowerWatts" REAL NULL,
    "BatteryPowerWatts" REAL NULL,
    "BatterySocPercent" REAL NULL,
    "PvProductionWatts" REAL NULL
);
CREATE INDEX IF NOT EXISTS "IX_LiveConsumptionSamples_MeasuredAtUtc" ON "LiveConsumptionSamples" ("MeasuredAtUtc");
""";
        }

        return """
CREATE TABLE IF NOT EXISTS "BatterySavingsDailySummaries" (
    "AccountingDate" TEXT NOT NULL,
    "Currency" TEXT NOT NULL,
    "GridChargedEnergyKwh" TEXT NOT NULL,
    "GridChargeCost" TEXT NOT NULL,
    "PvChargedEnergyKwh" TEXT NOT NULL,
    "PvOpportunityCost" TEXT NOT NULL,
    "DischargedEnergyKwh" TEXT NOT NULL,
    "DischargeAvoidedCost" TEXT NOT NULL,
    "NetSavings" TEXT NOT NULL,
    "UpdatedAtUtc" INTEGER NOT NULL,
    CONSTRAINT "PK_BatterySavingsDailySummaries" PRIMARY KEY ("AccountingDate", "Currency")
);
CREATE INDEX IF NOT EXISTS "IX_BatterySavingsDailySummaries_AccountingDate" ON "BatterySavingsDailySummaries" ("AccountingDate");
""";
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
