namespace TibberVictronController.Business.Models;

/// <summary>
/// Result of one Homematic polling cycle.
/// </summary>
public sealed class HomematicClimateSnapshot
{
    public required IReadOnlyList<HomematicClimateSample> Samples { get; init; }

    /// <summary>
    /// Gets device types that look climate-related but are not on the supported list,
    /// so operators can see which devices are silently skipped.
    /// </summary>
    public required IReadOnlyList<string> IgnoredClimateDeviceTypes { get; init; }
}
