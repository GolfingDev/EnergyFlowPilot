using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Entities;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Repositories;

public sealed class EfDecisionLogRepository : IDecisionLogRepository
{
    private readonly ControllerDbContext dbContext;

    public EfDecisionLogRepository(ControllerDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<IReadOnlyList<TibberPriceForecastSlot>> GetHistoricalPricesAsync(
        HistoricalPriceRequest request, CancellationToken cancellationToken = default)
    {
        if (request.FromUtc.Offset != TimeSpan.Zero || request.ToUtc.Offset != TimeSpan.Zero || request.ToUtc <= request.FromUtc)
            throw new ArgumentException("Der historische Preiszeitraum muss ein gueltiger UTC-Zeitraum sein.");
        // Aggregate in SQLite: reading every controller tick (and its JSON/reasons) for 14 days would be very expensive.
        // Source and currency are filtered before aggregation; untagged legacy logs belong exclusively to Tibber.
        var connection = dbContext.Database.GetDbConnection();
        var close = connection.State != System.Data.ConnectionState.Open;
        if (close) await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT (DecidedAtUtc / 900000) * 900000 AS SlotStart,
                       AVG(CAST(TibberPricePerKwh AS REAL)) AS Price
                FROM DecisionLogEntries
                WHERE DecidedAtUtc >= @from AND DecidedAtUtc < @to
                  AND TibberPricePerKwh IS NOT NULL AND TibberPriceCurrency = @currency
                  AND CASE WHEN json_valid(InputSummaryJson) AND COALESCE(json_extract(InputSummaryJson, '$.IsPriceEstimated'), 0) = 0
                      THEN COALESCE(json_extract(InputSummaryJson, '$.ElectricityPriceProvider'),
                          CASE WHEN json_extract(InputSummaryJson, '$.ControlSource') IS NULL THEN 'tibber' ELSE NULL END)
                      ELSE NULL END = @source
                GROUP BY SlotStart ORDER BY SlotStart
                """;
            foreach (var (name, value) in new (string, object)[] { ("@from", request.FromUtc.ToUnixTimeMilliseconds()),
                ("@to", request.ToUtc.ToUnixTimeMilliseconds()), ("@currency", request.Currency), ("@source", request.Source) })
            {
                var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            var result = new List<TibberPriceForecastSlot>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var start = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0));
                result.Add(new(new(start, start.AddMinutes(15)), (decimal)reader.GetDouble(1), request.Currency));
            }
            return result;
        }
        finally { if (close) await dbContext.Database.CloseConnectionAsync(); }
    }

    public async Task SaveDecisionAsync(
        DecisionLogEntry decisionLogEntry,
        CancellationToken cancellationToken = default)
    {
        dbContext.DecisionLogEntries.Add(MapToEntity(decisionLogEntry));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DecisionLogEntry>> GetRecentDecisionsAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), "Die Anzahl der Decision-Log-Eintraege muss groesser als 0 sein.");
        }

        return await dbContext.DecisionLogEntries
            .Include(logEntry => logEntry.Reasons)
            .OrderByDescending(logEntry => logEntry.DecidedAtUtc)
            .Take(maxCount)
            .Select(logEntry => MapToDomain(logEntry))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DecisionLogEntry>> GetDecisionsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Decision-Log-Zeitraum muss in UTC angegeben sein.");
        }

        if (toUtc <= fromUtc)
        {
            throw new ArgumentException("Das Decision-Log-Ende muss nach dem Start liegen.", nameof(toUtc));
        }

        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), "Die Anzahl der Decision-Log-Eintraege muss groesser als 0 sein.");
        }

        return await dbContext.DecisionLogEntries
            .Include(logEntry => logEntry.Reasons)
            .Where(logEntry => logEntry.DecidedAtUtc >= fromUtc && logEntry.DecidedAtUtc <= toUtc)
            .OrderBy(logEntry => logEntry.DecidedAtUtc)
            .Take(maxCount)
            .Select(logEntry => MapToDomain(logEntry))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<int> DeleteDecisionsOlderThanAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        if (cutoffUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Decision-Log-Aufraeumzeitpunkt muss in UTC angegeben sein.", nameof(cutoffUtc));
        }

        var oldEntries = await dbContext.DecisionLogEntries
            .Where(logEntry => logEntry.DecidedAtUtc < cutoffUtc)
            .ToArrayAsync(cancellationToken);

        dbContext.DecisionLogEntries.RemoveRange(oldEntries);
        await dbContext.SaveChangesAsync(cancellationToken);

        return oldEntries.Length;
    }

    private static DecisionLogEntryEntity MapToEntity(DecisionLogEntry decisionLogEntry)
    {
        return new DecisionLogEntryEntity
        {
            Id = decisionLogEntry.Id,
            DecidedAtUtc = decisionLogEntry.DecidedAtUtc,
            ValidFromUtc = decisionLogEntry.ValidFromUtc,
            ValidToUtc = decisionLogEntry.ValidToUtc,
            DecisionState = decisionLogEntry.Decision.Instruction.DecisionState,
            ChargeSource = decisionLogEntry.Decision.Instruction.ChargeSource,
            TargetPowerWatts = decisionLogEntry.Decision.TargetPowerWatts,
            StateOfChargePercent = decisionLogEntry.StateOfChargePercent,
            TibberPricePerKwh = decisionLogEntry.TibberPricePerKwh,
            TibberPriceCurrency = decisionLogEntry.TibberPriceCurrency,
            GridImportWatts = decisionLogEntry.GridImportWatts,
            GridExportWatts = decisionLogEntry.GridExportWatts,
            InputSummaryJson = decisionLogEntry.InputSummaryJson,
            Reasons = decisionLogEntry.Reasons
                .Select(reason => new DecisionLogReasonEntity
                {
                    RuleName = reason.RuleName,
                    Message = reason.Message
                })
                .ToList()
        };
    }

    private static DecisionLogEntry MapToDomain(DecisionLogEntryEntity logEntry)
    {
        var instruction = new BatteryDecisionInstruction(logEntry.DecisionState, logEntry.ChargeSource);
        var currentDecision = new CurrentBatteryDecision(instruction, logEntry.TargetPowerWatts);
        var reasons = logEntry.Reasons
            .OrderBy(reason => reason.Id)
            .Select(reason => new BatteryDecisionReason(reason.RuleName, reason.Message))
            .ToArray();

        return new DecisionLogEntry(
            logEntry.Id,
            logEntry.DecidedAtUtc,
            logEntry.ValidFromUtc,
            logEntry.ValidToUtc,
            currentDecision,
            logEntry.StateOfChargePercent,
            logEntry.TibberPricePerKwh,
            logEntry.TibberPriceCurrency,
            logEntry.GridImportWatts,
            logEntry.GridExportWatts,
            logEntry.InputSummaryJson,
            reasons);
    }
}
