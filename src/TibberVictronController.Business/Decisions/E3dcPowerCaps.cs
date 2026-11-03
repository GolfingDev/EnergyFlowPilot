namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Combines the limits that may cap E3/DC requests. The controller never writes a permanent limit to the device;
/// an own limit only lowers what the planner and the live limiter request.
/// </summary>
public static class E3dcPowerCaps
{
    /// <param name="reportedLimitWatts">Effective device limit if it is respected, otherwise null.</param>
    /// <param name="configuredLimitWatts">Own limit from the settings; 0 or less means none.</param>
    /// <returns>The stricter limit, or null when only the E3/DC system limit applies.</returns>
    public static int? Combine(int? reportedLimitWatts, int configuredLimitWatts)
    {
        int? configured = configuredLimitWatts > 0 ? configuredLimitWatts : null;
        return reportedLimitWatts is { } reported && configured is { } own ? Math.Min(reported, own) : reportedLimitWatts ?? configured;
    }
}
