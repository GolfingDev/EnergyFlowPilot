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
}
