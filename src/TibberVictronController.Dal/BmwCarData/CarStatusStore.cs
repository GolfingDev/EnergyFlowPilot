using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.BmwCarData;

public sealed class CarStatusStore : ICarStatusSource
{
    private readonly object gate = new();
    private CarSnapshot car = CarSnapshot.Empty;
    private CarDataConnectionState connection = CarDataConnectionState.NotConfigured;
    private CarStreamDiagnostics stream = CarStreamDiagnostics.Empty;
    private CarPollDiagnostics poll = CarPollDiagnostics.Empty;

    public CarPollDiagnostics Poll
    {
        get { lock (gate) return poll; }
    }

    public void SetPoll(CarPollDiagnostics diagnostics)
    {
        lock (gate) poll = diagnostics;
    }

    public CarStreamDiagnostics Stream
    {
        get { lock (gate) return stream; }
    }

    public void UpdateStream(Func<CarStreamDiagnostics, CarStreamDiagnostics> change)
    {
        lock (gate) stream = change(stream);
    }

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
