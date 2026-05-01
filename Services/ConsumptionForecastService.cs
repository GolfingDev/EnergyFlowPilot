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
    private readonly ForecastOptions _options;

    public ConsumptionForecastService(AppDbContext dbContext, IOptions<ForecastOptions> options)
    {
        _dbContext = dbContext;
        _options = options.Value;
    }

    public async Task<double> GetExpectedConsumptionWhAsync(DateTimeOffset targetTime, CancellationToken cancellationToken = default)
    {
        var targetUtc = targetTime.UtcDateTime;
        var configured = GetConfiguredLoad(targetUtc.Hour);
        var historical = await GetHistoricalLoadAsync(targetUtc, cancellationToken);

        if (configured.HasValue && historical.HasValue)
        {
            return (configured.Value * (1.0 - _options.HistoricalLoadWeight)) + (historical.Value * _options.HistoricalLoadWeight);
        }

        if (configured.HasValue)
        {
            return configured.Value;
        }

        if (historical.HasValue)
        {
            return historical.Value;
        }

        return _options.DefaultHouseConsumptionWatts;
    }

    private double? GetConfiguredLoad(int hour)
    {
        var block = _options.LoadBlocks.FirstOrDefault(x => hour >= x.StartHour && hour < x.EndHour);
        return block?.HouseConsumptionWatts;
    }

    private async Task<double?> GetHistoricalLoadAsync(DateTime targetUtc, CancellationToken cancellationToken)
    {
        var cutoff = targetUtc.AddDays(-30);
        var raw = await _dbContext.EnergyStateHistory
            .Where(x => x.TimestampUtc >= cutoff)
            .Select(x => new { x.TimestampUtc, x.HouseConsumptionWatts })
            .ToListAsync(cancellationToken);

        var targetQuarter = targetUtc.Minute / 15;

        var sameSlot = raw
            .Where(x => x.TimestampUtc.DayOfWeek == targetUtc.DayOfWeek
                && x.TimestampUtc.Hour == targetUtc.Hour
                && (x.TimestampUtc.Minute / 15) == targetQuarter)
            .Select(x => x.HouseConsumptionWatts)
            .ToList();

        if (sameSlot.Count > 0)
        {
            return sameSlot.Average();
        }

        var sameQuarter = raw
            .Where(x => x.TimestampUtc.Hour == targetUtc.Hour
                && (x.TimestampUtc.Minute / 15) == targetQuarter)
            .Select(x => x.HouseConsumptionWatts)
            .ToList();

        if (sameQuarter.Count > 0)
        {
            return sameQuarter.Average();
        }

        var sameHour = raw
            .Where(x => x.TimestampUtc.Hour == targetUtc.Hour)
            .Select(x => x.HouseConsumptionWatts)
            .ToList();

        return sameHour.Count > 0 ? sameHour.Average() : null;
    }
}
