using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TibberVictronController.Web.Data;

namespace TibberVictronController.Web.Services;

public interface IConsumptionForecastService
{
    Task<double> GetExpectedConsumptionWhAsync(DateTimeOffset targetTime, CancellationToken cancellationToken = default);
}

public class ConsumptionForecastService : IConsumptionForecastService
{
    private readonly AppDbContext _dbContext;
    private readonly ForecastOptions _forecastOptions;

    public ConsumptionForecastService(
        AppDbContext dbContext,
        IOptions<ForecastOptions> forecastOptions)
    {
        _dbContext = dbContext;
        _forecastOptions = forecastOptions.Value;
    }

    public async Task<double> GetExpectedConsumptionWhAsync(DateTimeOffset targetTime, CancellationToken cancellationToken = default)
    {
        var hour = targetTime.Hour;

        var configuredBlock = _forecastOptions.ConsumptionBlocks
            .FirstOrDefault(x => hour >= x.StartHour && hour < x.EndHour);

        if (configuredBlock is not null)
        {
            return configuredBlock.HouseConsumptionWatts;
        }

        if (_forecastOptions.UseConfiguredBlocksOnly)
        {
            return _forecastOptions.DefaultHouseConsumptionWatts;
        }

        var targetUtc = targetTime.UtcDateTime;
        var weekday = targetUtc.DayOfWeek;
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
            .Where(x => x.TimestampUtc.DayOfWeek == weekday && x.TimestampUtc.Hour == targetUtc.Hour)
            .Select(x => x.HouseConsumptionWatts)
            .ToList();

        if (sameSlot.Count > 0)
        {
            return sameSlot.Average();
        }

        var fallback = raw
            .Select(x => x.HouseConsumptionWatts)
            .ToList();

        return fallback.Count > 0 ? fallback.Average() : _forecastOptions.DefaultHouseConsumptionWatts;
    }
}
