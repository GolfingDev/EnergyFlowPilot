namespace TibberVictronController.Business.Models;

/// <summary>
/// Contains the reporting context for battery savings accounting.
/// </summary>
public sealed class BatterySavingsCalculationOptions
{
    public TimeZoneInfo? ReportingTimeZone { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }
}
