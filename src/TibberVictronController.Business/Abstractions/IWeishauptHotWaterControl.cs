using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

public interface IWeishauptHotWaterControl
{
    Task<WeishauptHotWaterReading> ReadAsync(CancellationToken cancellationToken);
    Task SetNormalTemperatureAsync(WeishauptHotWaterReading expected, decimal targetCelsius, CancellationToken cancellationToken);
}

public interface IWeishauptHotWaterStateStore
{
    Task<WeishauptHotWaterState> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(WeishauptHotWaterState state, CancellationToken cancellationToken);
}
