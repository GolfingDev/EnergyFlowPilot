namespace TibberVictronController.Business.Models;

/// <summary>
/// Contains battery settings that influence Decision Engine calculations.
/// </summary>
public sealed record BatteryConfiguration
{
    /// <summary>
    /// Validates persisted battery settings before the Decision Engine can use them.
    /// </summary>
    public BatteryConfiguration(
        decimal totalCapacityKwh,
        decimal minimumStateOfChargePercent = 10m,
        int maximumChargePowerWatts = 3000,
        int maximumDischargePowerWatts = 3000,
        decimal roundTripEfficiencyPercent = 90m)
    {
        if (totalCapacityKwh <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(totalCapacityKwh), "Die Batteriekapazitaet muss groesser als 0 kWh sein.");
        }

        if (minimumStateOfChargePercent is < 0m or >= 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumStateOfChargePercent), "Der minimale Akkuladestand muss zwischen 0 und kleiner 100 Prozent liegen.");
        }

        if (maximumChargePowerWatts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChargePowerWatts), "Die maximale Ladeleistung muss groesser als 0 Watt sein.");
        }

        if (maximumDischargePowerWatts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDischargePowerWatts), "Die maximale Entladeleistung muss groesser als 0 Watt sein.");
        }

        if (roundTripEfficiencyPercent is <= 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(roundTripEfficiencyPercent), "Der Batterie-Wirkungsgrad muss groesser als 0 und hoechstens 100 Prozent sein.");
        }

        TotalCapacityKwh = totalCapacityKwh;
        MinimumStateOfChargePercent = minimumStateOfChargePercent;
        MaximumChargePowerWatts = maximumChargePowerWatts;
        MaximumDischargePowerWatts = maximumDischargePowerWatts;
        RoundTripEfficiencyPercent = roundTripEfficiencyPercent;
    }

    /// <summary>
    /// Gets the total configured battery capacity in kWh.
    /// </summary>
    public decimal TotalCapacityKwh { get; }

    /// <summary>
    /// Gets the configured lower state-of-charge boundary that should be preserved for battery protection.
    /// </summary>
    public decimal MinimumStateOfChargePercent { get; }

    /// <summary>
    /// Gets the maximum charging power the Decision Engine may plan for.
    /// </summary>
    public int MaximumChargePowerWatts { get; }

    /// <summary>
    /// Gets the maximum discharging power the Decision Engine may plan for.
    /// </summary>
    public int MaximumDischargePowerWatts { get; }

    /// <summary>
    /// Gets the configured round-trip efficiency used by future cost optimization.
    /// </summary>
    public decimal RoundTripEfficiencyPercent { get; }
}
