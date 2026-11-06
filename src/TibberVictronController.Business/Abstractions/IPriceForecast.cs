using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>Exchange price forecast beyond the published auction (energyforecast.de).</summary>
public interface IPriceForecastSource
{
    /// <summary>Null when no access token is configured.</summary>
    Task<IReadOnlyList<PriceForecastPoint>?> GetAsync(CancellationToken cancellationToken = default);
}

public interface IPriceForecastSnapshotRepository
{
    /// <summary>Keeps the latest value per quarter hour and lead day; a newer one with the same key replaces it.</summary>
    Task UpsertAsync(IReadOnlyCollection<PriceForecastSnapshot> snapshots, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PriceForecastSnapshot>> GetAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);
}
