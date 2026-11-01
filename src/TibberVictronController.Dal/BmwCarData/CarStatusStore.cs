using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.BmwCarData;

public sealed class CarStatusStore : ICarStatusSource
{
    private readonly object gate = new();
    private CarSnapshot car = CarSnapshot.Empty;
    private CarDataConnectionState connection = CarDataConnectionState.NotConfigured;

    public CarSnapshot Car
    {
        get { lock (gate) return car; }
    }

    public CarDataConnectionState Connection
    {
        get { lock (gate) return connection; }
    }

    public CarSnapshot UpdateCar(Func<CarSnapshot, CarSnapshot> change)
    {
        lock (gate) return car = change(car);
    }

    public void SetConnection(CarDataConnectionState state)
    {
        lock (gate) connection = state;
    }
}
