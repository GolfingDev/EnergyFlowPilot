namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents one battery action that can contribute to monetary savings accounting.
/// </summary>
public sealed record BatterySavingsSlotMovement
{
    public ForecastTimeSlot TimeSlot { get; init; } = null!;

    public BatteryDecisionInstruction Instruction { get; init; } = null!;

    public int TargetPowerWatts { get; init; }

    public decimal TibberPricePerKwh { get; init; }

    public string Currency { get; init; } = string.Empty;

    public decimal PvSalePricePerKwh { get; init; }
}
