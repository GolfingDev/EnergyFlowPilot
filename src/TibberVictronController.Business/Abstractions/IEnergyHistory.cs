using TibberVictronController.Business.Energy;

namespace TibberVictronController.Business.Abstractions;

/// <summary>Raw measurements for the energy history: decision log and live samples, ordered by time.</summary>
public interface IEnergyMeasurementReader
{
    Task<IReadOnlyList<EnergyPowerPoint>> GetDecisionLogPointsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnergyPowerPoint>> GetLivePointsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);

    /// <summary>Time of the oldest decision log entry, where the history can start; null without any.</summary>
    Task<DateTimeOffset?> GetFirstMeasurementAtUtcAsync(CancellationToken cancellationToken = default);
}

/// <summary>Permanently stored quarter hours (kept when the raw data is deleted).</summary>
public interface IEnergyQuarterHourRepository
{
    Task<IReadOnlyList<EnergyQuarterHour>> GetAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);

    /// <summary>Start of the first quarter hour with a measured house balance; null before the first one.</summary>
    Task<DateTimeOffset?> GetFirstBalanceSlotAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces the given quarter hours (by slot start).</summary>
    Task SaveAsync(IReadOnlyCollection<EnergyQuarterHour> quarterHours, CancellationToken cancellationToken = default);
}
