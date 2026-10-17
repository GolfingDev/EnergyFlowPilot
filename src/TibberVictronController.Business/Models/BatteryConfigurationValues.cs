namespace TibberVictronController.Business.Models;

/// <summary>
/// Provides readable assignment-based input for battery configuration.
/// </summary>
public sealed class BatteryConfigurationValues
{
    public decimal TotalCapacityKwh { get; init; }

    public decimal MinimumStateOfChargePercent { get; init; } = 10m;

    public int MaximumChargePowerWatts { get; init; } = 3000;

    public int MaximumDischargePowerWatts { get; init; } = 3000;

    public decimal RoundTripEfficiencyPercent { get; init; } = 90m;

    public decimal? TargetEndStateOfChargePercent { get; init; }

    public decimal? PlanningMinimumStateOfChargePercent { get; init; }

    public decimal? PlanningMaximumStateOfChargePercent { get; init; }

    /// <summary>
    /// Gets the battery ageing cost per kWh that an additional grid-charged cycle causes.
    /// </summary>
    public decimal WearCostPerKwh { get; init; }

    /// <summary>
    /// Gets the minimum advantage per kWh a hold or grid charge must bring before the planner uses it.
    /// </summary>
    public decimal MinimumSavingsPerKwh { get; init; } = 0.01m;
}
