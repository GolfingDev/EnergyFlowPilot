namespace TibberVictronController.Business.Models;

/// <summary>
/// Defines the three operating states the Decision Engine may produce.
/// </summary>
public enum BatteryDecisionState
{
    Charge = 1,
    Discharge = 2,
    Idle = 3
}
