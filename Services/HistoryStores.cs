using Microsoft.EntityFrameworkCore;
using TibberVictronController.Web.Data;
using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Services;

public interface IDecisionHistoryStore
{
    Task AddAsync(Decision decision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DecisionHistoryEntry>> GetLast24HoursAsync(CancellationToken cancellationToken = default);
}

public interface IEnergyStateHistoryStore
{
    Task AddAsync(EnergyState state, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EnergyStateHistoryEntry>> GetLast24HoursAsync(CancellationToken cancellationToken = default);
}

public class SqliteDecisionHistoryStore : IDecisionHistoryStore
{
    private readonly AppDbContext _dbContext;

    public SqliteDecisionHistoryStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(Decision decision, CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var cutoffUtc = nowUtc.AddHours(-24);

        _dbContext.DecisionHistory.Add(new DecisionHistoryEntry
        {
            TimestampUtc = nowUtc,
            Action = decision.Action.ToString(),
            TargetPowerWatts = decision.TargetPowerWatts,
            CurrentPrice = decision.CurrentPrice,
            Reason = decision.Reason
        });

        var oldEntries = await _dbContext.DecisionHistory
            .Where(x => x.TimestampUtc < cutoffUtc)
            .ToListAsync(cancellationToken);

        if (oldEntries.Count > 0)
        {
            _dbContext.DecisionHistory.RemoveRange(oldEntries);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DecisionHistoryEntry>> GetLast24HoursAsync(CancellationToken cancellationToken = default)
    {
        var cutoffUtc = DateTime.UtcNow.AddHours(-24);

        return await _dbContext.DecisionHistory
            .Where(x => x.TimestampUtc >= cutoffUtc)
            .OrderByDescending(x => x.TimestampUtc)
            .ToListAsync(cancellationToken);
    }
}

public class SqliteEnergyStateHistoryStore : IEnergyStateHistoryStore
{
    private readonly AppDbContext _dbContext;

    public SqliteEnergyStateHistoryStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(EnergyState state, CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var cutoffUtc = nowUtc.AddHours(-24);

        _dbContext.EnergyStateHistory.Add(new EnergyStateHistoryEntry
        {
            TimestampUtc = nowUtc,
            GridPowerWatts = state.GridPowerWatts,
            BatterySocPercent = state.BatterySocPercent,
            BatteryPowerWatts = state.BatteryPowerWatts,
            HouseConsumptionWatts = state.HouseConsumptionWatts,
            PvPowerWatts = state.PvPowerWatts
        });

        var oldEntries = await _dbContext.EnergyStateHistory
            .Where(x => x.TimestampUtc < cutoffUtc)
            .ToListAsync(cancellationToken);

        if (oldEntries.Count > 0)
        {
            _dbContext.EnergyStateHistory.RemoveRange(oldEntries);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EnergyStateHistoryEntry>> GetLast24HoursAsync(CancellationToken cancellationToken = default)
    {
        var cutoffUtc = DateTime.UtcNow.AddHours(-24);

        return await _dbContext.EnergyStateHistory
            .Where(x => x.TimestampUtc >= cutoffUtc)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync(cancellationToken);
    }
}