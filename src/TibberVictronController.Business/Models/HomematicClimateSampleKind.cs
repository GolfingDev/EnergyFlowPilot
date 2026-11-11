namespace TibberVictronController.Business.Models;

/// <summary>
/// Classifies where a Homematic climate sample comes from, so analyses can separate room views,
/// single radiators, underfloor heating zones and the outdoor reference.
/// </summary>
public enum HomematicClimateSampleKind
{
    HeatingGroup,
    RadiatorThermostat,
    WallThermostat,
    UnderfloorHeatingZone,
    WeatherStation,

    /// <summary>Indoor temperature/humidity sensor without own control (HmIP-STH, HmIP-STHD).</summary>
    RoomSensor
}
