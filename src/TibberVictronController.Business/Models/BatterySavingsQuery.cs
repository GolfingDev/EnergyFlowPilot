namespace TibberVictronController.Business.Models;

/// <summary>
/// Defines a date range for battery savings summary queries.
/// </summary>
public sealed record BatterySavingsQuery
{
    public DateOnly StartDate { get; init; }

    public DateOnly EndDate { get; init; }

    public string Currency { get; init; } = "EUR";
}
