using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>Car values and CarData link state for the car page and, later, the charging planner.</summary>
public interface ICarStatusSource
{
    CarSnapshot Car { get; }

    CarDataConnectionState Connection { get; }
}
