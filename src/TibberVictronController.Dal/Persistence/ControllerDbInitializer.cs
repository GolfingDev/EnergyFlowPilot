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
        await EnsurePerBatterySavingsAsync(cancellationToken);
        await EnsureLiveConsumptionSampleColumnsAsync(cancellationToken);
        await EnsureWeishauptEnergyColumnsAsync(cancellationToken);
        await EnsureEvTripColumnsAsync(cancellationToken);
        await EnsureEvChargingSessionColumnsAsync(cancellationToken);
        await SeedMissingDefaultSettingsAsync(initializedAtUtc, cancellationToken);
    }

    // Additive migration: keep the old table intact and import its rows explicitly as Victron-only.
    private async Task EnsurePerBatterySavingsAsync(CancellationToken token)
    {
        await dbContext.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS "BatterySavingsByBattery" (
    "BatteryId" TEXT NOT NULL,
    "MeasuredSeconds" TEXT NOT NULL DEFAULT '0',
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
    PRIMARY KEY ("BatteryId", "AccountingDate", "Currency")
);
INSERT OR IGNORE INTO "BatterySavingsByBattery" ("BatteryId", "MeasuredSeconds", "AccountingDate", "Currency", "GridChargedEnergyKwh", "GridChargeCost", "PvChargedEnergyKwh", "PvOpportunityCost", "DischargedEnergyKwh", "DischargeAvoidedCost", "NetSavings", "UpdatedAtUtc")
SELECT 'victron', '0', "AccountingDate", "Currency", "GridChargedEnergyKwh", "GridChargeCost", "PvChargedEnergyKwh", "PvOpportunityCost", "DischargedEnergyKwh", "DischargeAvoidedCost", "NetSavings", "UpdatedAtUtc" FROM "BatterySavingsDailySummaries";
""", token);
    }

    // Additive migration: the base state of charge answer per charging session (2026-11-03).
    private async Task EnsureEvChargingSessionColumnsAsync(CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync("EvChargingSessions", cancellationToken)) return;
        var existing = await GetColumnNamesAsync("EvChargingSessions", cancellationToken);
        var columns = new Dictionary<string, string>
        {
            ["BaseChargeMode"] = "TEXT NULL",
            ["BaseChoiceAskedAtUtc"] = "INTEGER NULL",
            ["BaseChoiceBy"] = "TEXT NULL",
            ["IsStoppedByUser"] = "INTEGER NOT NULL DEFAULT 0",
            ["StoppedBy"] = "TEXT NULL",
            ["ImmediateTargetPercent"] = "REAL NULL",
            ["ImmediateTargetBy"] = "TEXT NULL"
        };
        foreach (var column in columns.Where(column => !existing.Contains(column.Key)))
            await dbContext.Database.ExecuteSqlRawAsync($"ALTER TABLE \"EvChargingSessions\" ADD COLUMN \"{column.Key}\" {column.Value};", cancellationToken);
    }

    // Additive migration: trip tables created by an earlier release get the newer columns with neutral defaults.
    private async Task EnsureEvTripColumnsAsync(CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync("EvTrips", cancellationToken)) return;
        var existing = await GetColumnNamesAsync("EvTrips", cancellationToken);
        var columns = new Dictionary<string, string>
        {
            ["RecurrenceDays"] = "INTEGER NOT NULL DEFAULT 0",
            ["RecurrenceUntilUtc"] = "INTEGER NULL",
            ["Source"] = "TEXT NOT NULL DEFAULT 'Manual'",
            ["ExternalId"] = "TEXT NULL",
            ["Subject"] = "TEXT NULL",
            ["IsIgnored"] = "INTEGER NOT NULL DEFAULT 0",
            ["SkippedDates"] = "TEXT NULL",
            ["RouteVersion"] = "INTEGER NOT NULL DEFAULT 0",
            ["HasLocationOverride"] = "INTEGER NOT NULL DEFAULT 0",
            ["OwnerId"] = "TEXT NULL",
            ["OwnerName"] = "TEXT NULL"
        };
        foreach (var column in columns.Where(column => !existing.Contains(column.Key)))
            await dbContext.Database.ExecuteSqlRawAsync($"ALTER TABLE \"EvTrips\" ADD COLUMN \"{column.Key}\" {column.Value};", cancellationToken);
    }

    private async Task EnsureWeishauptEnergyColumnsAsync(CancellationToken cancellationToken)
    {
        var existing = await GetColumnNamesAsync("WeishauptBiblockTelemetrySamples", cancellationToken);
        // Additive migration: historical rows retain null; no energy is backfilled.
        string[] columns =
        [
            "TotalHeatTodayKwh",
            "TotalHeatYesterdayKwh",
            "TotalHeatMonthKwh",
            "TotalHeatYearKwh",
            "HeatingHeatTodayKwh",
            "HeatingHeatYesterdayKwh",
            "HeatingHeatMonthKwh",
            "HeatingHeatYearKwh",
            "HotWaterHeatTodayKwh",
            "HotWaterHeatYesterdayKwh",
            "HotWaterHeatMonthKwh",
            "HotWaterHeatYearKwh",
            "ElectricalTodayKwh",
            "ElectricalYesterdayKwh",
            "ElectricalMonthKwh",
            "ElectricalYearKwh",
            "PowerDemandPercent"
        ];
        foreach (var column in columns)
            if (!existing.Contains(column))
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"WeishauptBiblockTelemetrySamples\" ADD COLUMN \"{column}\" INTEGER NULL;", cancellationToken);
    }

    private async Task EnsureMissingTablesAsync(CancellationToken cancellationToken)
    {
        var databaseCreator = dbContext.GetService<IRelationalDatabaseCreator>();

        foreach (var tableName in new[] { "BatterySavingsDailySummaries", "LiveConsumptionSamples", "ConsumptionDayProfiles", "HomematicClimateSamples", "WeishauptBiblockTelemetrySamples", "WeishauptHotWaterControlStates", "ConsumptionDailySummaries", "EvChargingSessions", "EvTrips", "PvForecastSnapshots", "CarSamples", "UserCalendars", "WebPushSubscriptions", "WebPushKeys" })
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
            ["PvProductionWatts"] = "REAL NULL",
            ["SupplementaryBatteryPowerWatts"] = "REAL NULL",
            ["SupplementaryBatterySocPercent"] = "REAL NULL",
            ["SupplementaryPvProductionWatts"] = "REAL NULL",
            ["EvChargingWatts"] = "REAL NULL"
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
        if (tableName == "PvForecastSnapshots")
        {
            return """
CREATE TABLE IF NOT EXISTS "PvForecastSnapshots" (
    "SlotStartUtc" INTEGER NOT NULL CONSTRAINT "PK_PvForecastSnapshots" PRIMARY KEY,
    "ForecastKwh" REAL NOT NULL,
    "CapturedAtUtc" INTEGER NOT NULL
);
""";
        }
        if (tableName == "WebPushSubscriptions")
        {
            return """
CREATE TABLE IF NOT EXISTS "WebPushSubscriptions" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_WebPushSubscriptions" PRIMARY KEY AUTOINCREMENT,
    "Endpoint" TEXT NOT NULL,
    "P256dh" TEXT NOT NULL,
    "Auth" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "UserName" TEXT NOT NULL,
    "CreatedAtUtc" INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_WebPushSubscriptions_Endpoint" ON "WebPushSubscriptions" ("Endpoint");
""";
        }
        if (tableName == "WebPushKeys")
        {
            return """
CREATE TABLE IF NOT EXISTS "WebPushKeys" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_WebPushKeys" PRIMARY KEY,
    "PublicKey" TEXT NOT NULL,
    "ProtectedPrivateKey" TEXT NOT NULL
);
""";
        }
        if (tableName == "UserCalendars")
        {
            return """
CREATE TABLE IF NOT EXISTS "UserCalendars" (
    "UserId" TEXT NOT NULL CONSTRAINT "PK_UserCalendars" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Mailbox" TEXT NOT NULL,
    "ConnectedAtUtc" INTEGER NOT NULL
);
""";
        }
        if (tableName == "CarSamples")
        {
            return """
CREATE TABLE IF NOT EXISTS "CarSamples" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_CarSamples" PRIMARY KEY AUTOINCREMENT,
    "MeasuredAtUtc" INTEGER NOT NULL,
    "SocPercent" REAL NULL,
    "OdometerKm" REAL NULL
);
CREATE INDEX IF NOT EXISTS "IX_CarSamples_MeasuredAtUtc" ON "CarSamples" ("MeasuredAtUtc");
""";
        }

        if (tableName == "EvTrips")
        {
            return """
CREATE TABLE IF NOT EXISTS "EvTrips" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_EvTrips" PRIMARY KEY AUTOINCREMENT,
    "DepartureAtUtc" INTEGER NOT NULL,
    "ReturnAtUtc" INTEGER NULL,
    "Destination" TEXT NOT NULL,
    "ResolvedAddress" TEXT NOT NULL,
    "Latitude" REAL NOT NULL,
    "Longitude" REAL NOT NULL,
    "DistanceKm" REAL NOT NULL,
    "DurationMinutes" REAL NOT NULL,
    "CreatedAtUtc" INTEGER NOT NULL,
    "RecurrenceDays" INTEGER NOT NULL DEFAULT 0,
    "RecurrenceUntilUtc" INTEGER NULL,
    "Source" TEXT NOT NULL DEFAULT 'Manual',
    "ExternalId" TEXT NULL,
    "Subject" TEXT NULL,
    "IsIgnored" INTEGER NOT NULL DEFAULT 0,
    "SkippedDates" TEXT NULL,
    "RouteVersion" INTEGER NOT NULL DEFAULT 0,
    "HasLocationOverride" INTEGER NOT NULL DEFAULT 0,
    "OwnerId" TEXT NULL,
    "OwnerName" TEXT NULL
);
CREATE INDEX IF NOT EXISTS "IX_EvTrips_DepartureAtUtc" ON "EvTrips" ("DepartureAtUtc");
CREATE INDEX IF NOT EXISTS "IX_EvTrips_ExternalId" ON "EvTrips" ("ExternalId");
""";
        }

        if (tableName == "EvChargingSessions")
        {
            return """
CREATE TABLE IF NOT EXISTS "EvChargingSessions" (
    "TransactionId" INTEGER NOT NULL CONSTRAINT "PK_EvChargingSessions" PRIMARY KEY AUTOINCREMENT,
    "ChargePointId" TEXT NOT NULL,
    "ConnectorId" INTEGER NOT NULL,
    "IdTag" TEXT NOT NULL,
    "StartedAtUtc" INTEGER NOT NULL,
    "MeterStartWh" REAL NOT NULL,
    "StoppedAtUtc" INTEGER NULL,
    "MeterStopWh" REAL NULL,
    "StopReason" TEXT NULL,
    "LastMeterWh" REAL NULL,
    "LastMeterAtUtc" INTEGER NULL,
    "BaseChargeMode" TEXT NULL,
    "BaseChoiceAskedAtUtc" INTEGER NULL,
    "BaseChoiceBy" TEXT NULL,
    "IsStoppedByUser" INTEGER NOT NULL DEFAULT 0,
    "StoppedBy" TEXT NULL,
    "ImmediateTargetPercent" REAL NULL,
    "ImmediateTargetBy" TEXT NULL
);
CREATE INDEX IF NOT EXISTS "IX_EvChargingSessions_StartedAtUtc" ON "EvChargingSessions" ("StartedAtUtc");
""";
        }
        if (tableName == "WeishauptHotWaterControlStates")
        {
            return """
CREATE TABLE IF NOT EXISTS "WeishauptHotWaterControlStates" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_WeishauptHotWaterControlStates" PRIMARY KEY,
    "StateJson" TEXT NOT NULL
);
""";
        }
        if (string.Equals(tableName, "ConsumptionDailySummaries", StringComparison.OrdinalIgnoreCase))
        {
            return """
CREATE TABLE IF NOT EXISTS "ConsumptionDailySummaries" (
    "LocalDate" TEXT NOT NULL CONSTRAINT "PK_ConsumptionDailySummaries" PRIMARY KEY,
    "EnergyKwh" REAL NOT NULL,
    "SlotCount" INTEGER NOT NULL,
    "HeatingDegrees" REAL NOT NULL,
    "UpdatedAtUtc" INTEGER NOT NULL
);
""";
        }

        if (string.Equals(tableName, "HomematicClimateSamples", StringComparison.OrdinalIgnoreCase))
        {
            return """
CREATE TABLE IF NOT EXISTS "HomematicClimateSamples" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_HomematicClimateSamples" PRIMARY KEY AUTOINCREMENT,
    "PolledAtUtc" INTEGER NOT NULL,
    "LastChangedAtUtc" INTEGER NULL,
    "ChannelAddress" TEXT NOT NULL,
    "DeviceType" TEXT NOT NULL,
    "DeviceName" TEXT NOT NULL,
    "ChannelName" TEXT NOT NULL,
    "Kind" TEXT NOT NULL,
    "ActualTemperatureCelsius" REAL NULL,
    "SetPointTemperatureCelsius" REAL NULL,
    "SetPointMode" TEXT NULL,
    "ValveOpeningPercent" REAL NULL,
    "RelativeHumidityPercent" REAL NULL,
    "IsWindowOpen" INTEGER NULL,
    "IsReachable" INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_HomematicClimateSamples_PolledAtUtc" ON "HomematicClimateSamples" ("PolledAtUtc");
CREATE INDEX IF NOT EXISTS "IX_HomematicClimateSamples_ChannelAddress_PolledAtUtc" ON "HomematicClimateSamples" ("ChannelAddress", "PolledAtUtc");
""";
        }

        if (string.Equals(tableName, "WeishauptBiblockTelemetrySamples", StringComparison.OrdinalIgnoreCase))
        {
            return """
CREATE TABLE IF NOT EXISTS "WeishauptBiblockTelemetrySamples" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_WeishauptBiblockTelemetrySamples" PRIMARY KEY AUTOINCREMENT,
    "MeasuredAtUtc" INTEGER NOT NULL,
    "OutdoorTemperatureCelsius" REAL NOT NULL,
    "OperatingStatusCode" INTEGER NOT NULL,
    "HotWaterTemperatureCelsius" REAL NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_WeishauptBiblockTelemetrySamples_MeasuredAtUtc" ON "WeishauptBiblockTelemetrySamples" ("MeasuredAtUtc");
""";
        }

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
    "PvProductionWatts" REAL NULL,
    "SupplementaryBatteryPowerWatts" REAL NULL,
    "SupplementaryBatterySocPercent" REAL NULL,
    "SupplementaryPvProductionWatts" REAL NULL,
    "EvChargingWatts" REAL NULL
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
