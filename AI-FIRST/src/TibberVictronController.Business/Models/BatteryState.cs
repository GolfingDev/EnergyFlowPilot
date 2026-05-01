namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents the battery state that the Decision Engine uses as the starting point for forecasts and direct decisions.
/// </summary>
public sealed record BatteryState
{
    /// <summary>
    /// Validates battery telemetry before it becomes a Decision Engine input.
    /// </summary>
    public BatteryState(decimal stateOfChargePercent, DateTimeOffset measuredAtUtc)
    {
        if (stateOfChargePercent is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(stateOfChargePercent), "Der Akkuladestand muss zwischen 0 und 100 Prozent liegen.");
        }

        if (measuredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Messzeitpunkt des Akkuladestands muss in UTC angegeben sein.", nameof(measuredAtUtc));
        }

        StateOfChargePercent = stateOfChargePercent;
        MeasuredAtUtc = measuredAtUtc;
    }

    /// <summary>
    /// Gets the battery state of charge in percent.
    /// </summary>
    public decimal StateOfChargePercent { get; }

    /// <summary>
    /// Gets the UTC timestamp when the battery state was measured.
    /// </summary>
    public DateTimeOffset MeasuredAtUtc { get; }
}
