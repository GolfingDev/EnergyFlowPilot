namespace TibberVictronController.Business.Models;

/// <summary>
/// Unified thermostat operating mode across HmIP (SET_POINT_MODE/BOOST_MODE) and BidCos (CONTROL_MODE).
/// </summary>
public enum HomematicSetPointMode
{
    Automatic,
    Manual,
    Party,
    Boost
}
