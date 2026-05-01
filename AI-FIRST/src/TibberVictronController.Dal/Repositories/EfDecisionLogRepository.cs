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
