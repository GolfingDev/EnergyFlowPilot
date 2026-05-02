namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents the latest live site telemetry that the direct decision path must validate.
/// </summary>
public sealed record CurrentSiteTelemetry
{
    /// <summary>
    /// Validates the live telemetry snapshot before it becomes a direct decision input.
    /// </summary>
    public CurrentSiteTelemetry(
        int currentGridImportWatts,
        int currentPvProductionWatts,
        DateTimeOffset measuredAtUtc)
    {
        if (measuredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Messzeitpunkt der Live-Telemetrie muss in UTC angegeben sein.", nameof(measuredAtUtc));
        }

        CurrentGridImportWatts = currentGridImportWatts;
        CurrentPvProductionWatts = currentPvProductionWatts;
        MeasuredAtUtc = measuredAtUtc;
    }

    public int CurrentGridImportWatts { get; }

    public int CurrentPvProductionWatts { get; }

    public DateTimeOffset MeasuredAtUtc { get; }
}
