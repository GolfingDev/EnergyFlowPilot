using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Limits discharge target power so the Decision Engine never plans battery export into the grid.
/// </summary>
public sealed class BatteryDischargePowerLimiter
{
    /// <summary>
    /// Calculates a discharge setpoint that can cover grid import but cannot create grid feed-in.
    /// </summary>
    public int CalculateTargetPowerWatts(
        int currentGridImportWatts,
        BatteryConfiguration batteryConfiguration)
    {
        if (batteryConfiguration is null)
        {
            throw new ArgumentNullException(nameof(batteryConfiguration), "Die Batteriekonfiguration darf nicht null sein.");
        }

        if (currentGridImportWatts <= 0)
        {
            return 0;
        }

        return Math.Min(currentGridImportWatts, batteryConfiguration.MaximumDischargePowerWatts);
    }
}
