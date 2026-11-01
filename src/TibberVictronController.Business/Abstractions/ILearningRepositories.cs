using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

public interface IPvForecastSnapshotRepository
{
    /// <summary>Stores the latest forecast per quarter hour; a newer forecast for the same quarter hour replaces the older one.</summary>
    Task UpsertAsync(IReadOnlyCollection<PvForecastSnapshot> snapshots, DateTimeOffset capturedAtUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PvForecastSnapshot>> GetAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);
}

public interface ICarSampleRepository
{
    Task AddAsync(CarSample sample, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CarSample>> GetSinceAsync(DateTimeOffset fromUtc, CancellationToken cancellationToken = default);
}
