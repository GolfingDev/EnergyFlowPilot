namespace TibberVictronController.Business.Models;

/// <summary>
/// Defines the energy source used when the Decision Engine decides to charge the battery.
/// </summary>
public enum BatteryChargeSource
{
    Grid = 1,
    PV = 2
}
