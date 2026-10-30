namespace TibberVictronController.Business.Models;

/// <summary>
/// Defines a date range for battery savings summary queries.
/// </summary>
public sealed record BatterySavingsQuery
{
    /// <summary>Null selects both batteries; old callers retain the Victron scope.</summary>
    public string? BatteryId { get; init; } = "victron";

    public DateOnly StartDate { get; init; }

    public DateOnly EndDate { get; init; }

    public string Currency { get; init; } = "EUR";
}
