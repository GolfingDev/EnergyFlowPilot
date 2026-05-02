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
        decimal roundTripEfficiencyPercent = 90m,
        decimal? targetEndStateOfChargePercent = null)
        : this(new BatteryConfigurationValues
        {
            TotalCapacityKwh = totalCapacityKwh,
            MinimumStateOfChargePercent = minimumStateOfChargePercent,
            MaximumChargePowerWatts = maximumChargePowerWatts,
            MaximumDischargePowerWatts = maximumDischargePowerWatts,
            RoundTripEfficiencyPercent = roundTripEfficiencyPercent,
            TargetEndStateOfChargePercent = targetEndStateOfChargePercent
        })
    {
    }

    /// <summary>
    /// Validates assignment-based persisted battery settings before the Decision Engine can use them.
    /// </summary>
    public BatteryConfiguration(BatteryConfigurationValues values)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values), "Die Batteriekonfiguration darf nicht null sein.");
        }

        var totalCapacityKwh = values.TotalCapacityKwh;
        var minimumStateOfChargePercent = values.MinimumStateOfChargePercent;
        var maximumChargePowerWatts = values.MaximumChargePowerWatts;
        var maximumDischargePowerWatts = values.MaximumDischargePowerWatts;
        var roundTripEfficiencyPercent = values.RoundTripEfficiencyPercent;

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

        var configuredTargetEndStateOfChargePercent = values.TargetEndStateOfChargePercent ?? minimumStateOfChargePercent;
        var configuredPlanningMinimumStateOfChargePercent = values.PlanningMinimumStateOfChargePercent ?? minimumStateOfChargePercent;
        var configuredPlanningMaximumStateOfChargePercent = values.PlanningMaximumStateOfChargePercent ?? 100m;

        if (configuredTargetEndStateOfChargePercent < minimumStateOfChargePercent || configuredTargetEndStateOfChargePercent > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(values.TargetEndStateOfChargePercent), "Die Ziel-Endreserve muss zwischen minimalem Akkuladestand und 100 Prozent liegen.");
        }

        if (configuredPlanningMinimumStateOfChargePercent < minimumStateOfChargePercent || configuredPlanningMinimumStateOfChargePercent > configuredTargetEndStateOfChargePercent)
        {
            throw new ArgumentOutOfRangeException(nameof(values.PlanningMinimumStateOfChargePercent), "Die Planungsreserve muss zwischen minimalem Akkuladestand und Ziel-Endreserve liegen.");
        }

        if (configuredPlanningMaximumStateOfChargePercent < configuredTargetEndStateOfChargePercent || configuredPlanningMaximumStateOfChargePercent > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(values.PlanningMaximumStateOfChargePercent), "Das Planungs-Maximum muss zwischen Ziel-Endreserve und 100 Prozent liegen.");
        }

        TotalCapacityKwh = totalCapacityKwh;
        MinimumStateOfChargePercent = minimumStateOfChargePercent;
        MaximumChargePowerWatts = maximumChargePowerWatts;
        MaximumDischargePowerWatts = maximumDischargePowerWatts;
        RoundTripEfficiencyPercent = roundTripEfficiencyPercent;
        TargetEndStateOfChargePercent = configuredTargetEndStateOfChargePercent;
        PlanningMinimumStateOfChargePercent = configuredPlanningMinimumStateOfChargePercent;
        PlanningMaximumStateOfChargePercent = configuredPlanningMaximumStateOfChargePercent;
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

    /// <summary>
    /// Gets the configured reserve that should remain explainable at the end of the planning horizon.
    /// </summary>
    public decimal TargetEndStateOfChargePercent { get; }

    /// <summary>
    /// Gets the softer planning boundary used before the absolute battery protection limit is reached.
    /// </summary>
    public decimal PlanningMinimumStateOfChargePercent { get; }

    /// <summary>
    /// Gets the softer grid-charging boundary used to preserve headroom for possible PV forecast errors.
    /// </summary>
    public decimal PlanningMaximumStateOfChargePercent { get; }
}
