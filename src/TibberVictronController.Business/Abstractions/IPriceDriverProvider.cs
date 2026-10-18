using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Provides the inputs that drive the German day-ahead price: weather over Germany (past and forecast) and past spot prices.
/// </summary>
public interface IPriceDriverProvider
{
    /// <summary>
    /// Returns the current inputs, or null when they could not be loaded; callers then keep their simpler estimate.
    /// </summary>
    Task<PriceDriverData?> GetPriceDriversAsync(CancellationToken cancellationToken = default);
}
