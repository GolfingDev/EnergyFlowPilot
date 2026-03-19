using Microsoft.EntityFrameworkCore;
using TibberVictronController.Web.Data;

namespace TibberVictronController.Web.Services;

public interface IConsumptionForecastService
{
    Task<double> GetExpectedConsumptionWhAsync(DateTimeOffset targetTime, CancellationToken cancellationToken = default);
}

public class ConsumptionForecastService : IConsumptionForecastService
{
    private readonly AppDbContext _dbContext;

    public ConsumptionForecastService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<double> GetExpectedConsumptionWhAsync(DateTimeOffset targetTime, CancellationToken cancellationToken = default)
    {
        var targetUtc = targetTime.UtcDateTime;
        var weekday = targetUtc.DayOfWeek;
        var hour = targetUtc.Hour;
        var cutoff = targetUtc.AddDays(-30);

        var raw = await _dbContext.EnergyStateHistory
            .Where(x => x.TimestampUtc >= cutoff)
            .Select(x => new
            {
                x.TimestampUtc,
                x.HouseConsumptionWatts
            })
            .ToListAsync(cancellationToken);

        var sameSlot = raw
            .Where(x => x.TimestampUtc.DayOfWeek == weekday && x.TimestampUtc.Hour == hour)
            .Select(x => x.HouseConsumptionWatts)
            .ToList();

        if (sameSlot.Count > 0)
        {
            return sameSlot.Average();
        }

        var fallback = raw
            .Where(x => x.TimestampUtc >= targetUtc.AddHours(-24))
            .Select(x => x.HouseConsumptionWatts)
            .ToList();

        return fallback.Count > 0 ? fallback.Average() : 500;
    }
}